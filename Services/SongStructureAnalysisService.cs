using DrumPracticeStudio.Models;
using NAudio.Wave;

namespace DrumPracticeStudio.Services;

public sealed class SongStructureAnalysisService
{
    private const double FeatureSeconds = 1d;
    private const double MinimumSectionSeconds = 8d;
    private const int MaximumSections = 32;

    public Task<SongStructureMap> AnalyzeAsync(
        string path,
        TempoSettings? tempo,
        CancellationToken cancellationToken = default) => Task.Run(
        () => Analyze(path, tempo, cancellationToken),
        cancellationToken);

    public SongStructureMap Analyze(
        string path,
        TempoSettings? tempo,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new AudioFileReader(path);
        var features = ReadFeatures(reader, cancellationToken);
        var duration = reader.TotalTime.TotalSeconds;
        if (features.Count < 12 || duration < 12d)
        {
            throw new InvalidDataException(
                "La pista es demasiado corta para proponer una estructura musical.");
        }

        NormalizeFeatures(features);
        var novelty = BuildNovelty(features);
        var candidates = FindBoundaries(novelty, duration, tempo);
        var boundaries = new List<double> { 0d };
        foreach (var candidate in candidates)
        {
            if (candidate - boundaries[^1] >= MinimumSectionSeconds &&
                duration - candidate >= MinimumSectionSeconds)
            {
                boundaries.Add(candidate);
            }
        }
        boundaries.Add(duration);

        var sections = new List<SongSection>();
        var signatures = new List<(double Energy, double Activity, string Label)>();
        for (var index = 0; index < boundaries.Count - 1; index++)
        {
            var start = boundaries[index];
            var end = boundaries[index + 1];
            var featureStart = Math.Clamp((int)Math.Floor(start / FeatureSeconds), 0, features.Count - 1);
            var featureEnd = Math.Clamp((int)Math.Ceiling(end / FeatureSeconds), featureStart + 1, features.Count);
            var slice = features.Skip(featureStart).Take(featureEnd - featureStart).ToArray();
            var energy = slice.Average(feature => feature.Energy);
            var activity = slice.Average(feature => feature.Activity);
            var label = ResolveLabel(signatures, energy, activity);
            signatures.Add((energy, activity, label));
            var boundaryIndex = Math.Clamp((int)Math.Round(start / FeatureSeconds), 0, novelty.Length - 1);
            var confidence = index == 0
                ? 0.5d
                : Math.Clamp(0.35d + novelty[boundaryIndex] * 0.45d, 0.35d, 0.88d);
            sections.Add(new SongSection(
                Guid.NewGuid().ToString("N"),
                start,
                end,
                label,
                confidence,
                $"{energy:F3}|{activity:F3}"));
        }

        var overallConfidence = sections.Count == 0
            ? 0d
            : sections.Average(section => section.Confidence);
        return SongStructureMap.Normalize(new SongStructureMap(
            DateTimeOffset.UtcNow,
            duration,
            overallConfidence,
            sections));
    }

    private static List<Feature> ReadFeatures(
        AudioFileReader reader,
        CancellationToken cancellationToken)
    {
        var channels = reader.WaveFormat.Channels;
        var framesPerFeature = Math.Max(1, (int)Math.Round(
            reader.WaveFormat.SampleRate * FeatureSeconds));
        var buffer = new float[32_768 - (32_768 % channels)];
        var result = new List<Feature>();
        double energy = 0d;
        double activity = 0d;
        double previous = 0d;
        var frames = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = ((ISampleProvider)reader).Read(buffer);
            if (read == 0)
            {
                break;
            }
            for (var index = 0; index + channels <= read; index += channels)
            {
                double mono = 0d;
                for (var channel = 0; channel < channels; channel++)
                {
                    mono += buffer[index + channel];
                }
                mono /= channels;
                energy += mono * mono;
                activity += Math.Abs(mono - previous);
                previous = mono;
                frames++;
                if (frames < framesPerFeature)
                {
                    continue;
                }
                result.Add(new Feature(
                    Math.Sqrt(energy / frames),
                    activity / frames));
                energy = 0d;
                activity = 0d;
                frames = 0;
            }
        }
        if (frames > 0)
        {
            result.Add(new Feature(Math.Sqrt(energy / frames), activity / frames));
        }
        return result;
    }

    private static void NormalizeFeatures(IList<Feature> features)
    {
        var maximumEnergy = Math.Max(1e-9d, features.Max(feature => feature.Energy));
        var maximumActivity = Math.Max(1e-9d, features.Max(feature => feature.Activity));
        for (var index = 0; index < features.Count; index++)
        {
            features[index] = new Feature(
                features[index].Energy / maximumEnergy,
                features[index].Activity / maximumActivity);
        }
    }

    private static double[] BuildNovelty(IReadOnlyList<Feature> features)
    {
        var novelty = new double[features.Count];
        const int radius = 3;
        for (var index = radius; index < features.Count - radius; index++)
        {
            var leftEnergy = features.Skip(index - radius).Take(radius).Average(item => item.Energy);
            var rightEnergy = features.Skip(index).Take(radius).Average(item => item.Energy);
            var leftActivity = features.Skip(index - radius).Take(radius).Average(item => item.Activity);
            var rightActivity = features.Skip(index).Take(radius).Average(item => item.Activity);
            novelty[index] = Math.Sqrt(
                Math.Pow(rightEnergy - leftEnergy, 2d) +
                Math.Pow(rightActivity - leftActivity, 2d));
        }
        var maximum = Math.Max(1e-9d, novelty.Max());
        for (var index = 0; index < novelty.Length; index++)
        {
            novelty[index] /= maximum;
        }
        return novelty;
    }

    private static IReadOnlyList<double> FindBoundaries(
        IReadOnlyList<double> novelty,
        double duration,
        TempoSettings? tempo)
    {
        var positive = novelty.Where(value => value > 0d).Order().ToArray();
        var threshold = positive.Length == 0
            ? 1d
            : positive[(int)Math.Floor((positive.Length - 1) * 0.72d)];
        var ranked = new List<(double Time, double Strength)>();
        for (var index = 2; index < novelty.Count - 2; index++)
        {
            if (novelty[index] < threshold ||
                novelty[index] < novelty[index - 1] ||
                novelty[index] < novelty[index + 1])
            {
                continue;
            }
            var time = index * FeatureSeconds;
            time = tempo is null ? time : SnapToBar(time, tempo);
            if (time > 0d && time < duration)
            {
                ranked.Add((time, novelty[index]));
            }
        }
        return ranked
            .OrderByDescending(item => item.Strength)
            .Take(MaximumSections - 1)
            .OrderBy(item => item.Time)
            .Select(item => item.Time)
            .DistinctBy(time => Math.Round(time, 2))
            .ToArray();
    }

    private static double SnapToBar(double seconds, TempoSettings tempo)
    {
        tempo = TempoSettings.Normalize(tempo);
        var segment = tempo.GetSegmentAt(seconds);
        var barSeconds = 60d / segment.Bpm * segment.BeatsPerBar;
        var bar = Math.Round(
            (seconds - segment.FirstBeatSeconds) / barSeconds,
            MidpointRounding.AwayFromZero);
        return Math.Max(segment.StartSeconds, segment.FirstBeatSeconds + bar * barSeconds);
    }

    private static string ResolveLabel(
        IReadOnlyList<(double Energy, double Activity, string Label)> existing,
        double energy,
        double activity)
    {
        foreach (var signature in existing)
        {
            var distance = Math.Sqrt(
                Math.Pow(signature.Energy - energy, 2d) +
                Math.Pow(signature.Activity - activity, 2d));
            if (distance <= 0.18d)
            {
                return signature.Label;
            }
        }
        var index = existing.Select(item => item.Label).Distinct(StringComparer.Ordinal).Count();
        return $"Sección {ToAlphabeticLabel(index)}";
    }

    private static string ToAlphabeticLabel(int index)
    {
        index = Math.Max(0, index);
        var value = string.Empty;
        do
        {
            value = (char)('A' + (index % 26)) + value;
            index = (index / 26) - 1;
        } while (index >= 0);
        return value;
    }

    private readonly record struct Feature(double Energy, double Activity);
}
