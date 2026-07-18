using DrumPracticeStudio.Models;
using NAudio.Wave;

namespace DrumPracticeStudio.Services;

public sealed class TempoAnalysisService
{
    private const int EnvelopeRate = 200;
    private const double MinimumBpm = 60d;
    private const double MaximumBpm = 200d;
    private const int MapWindowSeconds = 24;
    private const int MapHopSeconds = 12;

    public Task<TempoAnalysisResult> AnalyzeAsync(
        string path,
        CancellationToken cancellationToken = default) => Task.Run(
        () => Analyze(path, cancellationToken),
        cancellationToken);

    public Task<TempoMapAnalysisResult> AnalyzeMapAsync(
        string path,
        CancellationToken cancellationToken = default) => Task.Run(
        () => AnalyzeMap(path, cancellationToken),
        cancellationToken);

    public TempoAnalysisResult Analyze(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new AudioFileReader(path);
        var envelope = BuildOnsetEnvelope(reader, cancellationToken);
        return AnalyzeEnvelope(envelope, 0d, cancellationToken);
    }

    public TempoMapAnalysisResult AnalyzeMap(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new AudioFileReader(path);
        var envelope = BuildOnsetEnvelope(reader, cancellationToken);
        if (envelope.Length < EnvelopeRate * 4 || envelope.Max() <= 1e-8d)
        {
            throw new InvalidDataException("La pista no contiene suficientes pulsos para estimar el tempo.");
        }

        var windowLength = EnvelopeRate * MapWindowSeconds;
        var hopLength = EnvelopeRate * MapHopSeconds;
        var raw = new List<TempoSegment>();
        for (var start = 0; start < envelope.Length; start += hopLength)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = Math.Min(windowLength, envelope.Length - start);
            if (length < EnvelopeRate * 8 && raw.Count > 0)
            {
                break;
            }

            var result = AnalyzeEnvelope(
                envelope.AsSpan(start, length).ToArray(),
                start / (double)EnvelopeRate,
                cancellationToken);
            var bpm = ResolveHalfDoubleTempo(raw.LastOrDefault()?.Bpm, result.Bpm);
            raw.Add(TempoSegment.Create(
                startSeconds: start / (double)EnvelopeRate,
                bpm: bpm,
                firstBeatSeconds: result.FirstBeatSeconds,
                confidence: result.Confidence,
                sourceName: "Análisis local por ventanas"));
        }

        var merged = MergeStableWindows(raw);
        var overallConfidence = merged.Count == 0
            ? 0d
            : merged.Average(segment => segment.Confidence);
        var summary = merged.Count switch
        {
            0 => "No se pudo proponer ningún tramo.",
            1 => $"Tempo estable propuesto: {merged[0].Bpm:0.##} BPM.",
            _ => $"{merged.Count} tramos propuestos; revisa los límites antes de aplicarlos."
        };
        return new TempoMapAnalysisResult(merged, overallConfidence, summary);
    }

    private static TempoAnalysisResult AnalyzeEnvelope(
        double[] envelope,
        double absoluteOffsetSeconds,
        CancellationToken cancellationToken)
    {
        if (envelope.Length < EnvelopeRate * 4 || envelope.Max() <= 1e-8d)
        {
            throw new InvalidDataException("La pista no contiene suficientes pulsos para estimar el tempo.");
        }

        var minimumLag = (int)Math.Floor(EnvelopeRate * 60d / MaximumBpm);
        var maximumLag = (int)Math.Ceiling(EnvelopeRate * 60d / MinimumBpm);
        var scores = new double[maximumLag + 1];
        var bestLag = minimumLag;
        var bestScore = double.MinValue;
        for (var lag = minimumLag; lag <= maximumLag; lag++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double numerator = 0d;
            double leftEnergy = 0d;
            double rightEnergy = 0d;
            for (var index = lag; index < envelope.Length; index++)
            {
                var left = envelope[index];
                var right = envelope[index - lag];
                numerator += left * right;
                leftEnergy += left * left;
                rightEnergy += right * right;
            }

            var score = numerator / Math.Sqrt(Math.Max(1e-20d, leftEnergy * rightEnergy));
            scores[lag] = score;
            if (score > bestScore)
            {
                bestScore = score;
                bestLag = lag;
            }
        }

        var onsetLag = EstimateOnsetInterval(envelope, minimumLag, maximumLag);
        if (onsetLag is { } observedLag && scores[observedLag] >= bestScore * 0.85d)
        {
            bestLag = observedLag;
            bestScore = scores[observedLag];
        }

        var bpm = 60d * EnvelopeRate / bestLag;
        var searchLimit = Math.Min(envelope.Length, EnvelopeRate * 30);
        var maximumOnset = envelope.Take(searchLimit).Max();
        var threshold = maximumOnset * 0.55d;
        var firstOnset = Array.FindIndex(envelope, 0, searchLimit, value => value >= threshold);
        if (firstOnset < 0)
        {
            firstOnset = Array.IndexOf(envelope, maximumOnset);
        }

        var sortedScores = scores
            .Skip(minimumLag)
            .Where(score => score > 0d)
            .OrderDescending()
            .Take(8)
            .ToArray();
        var competing = sortedScores.Length > 1 ? sortedScores[1] : 0d;
        var distinctness = bestScore <= 0d ? 0d : Math.Clamp((bestScore - competing) / bestScore, 0d, 1d);
        var confidence = Math.Clamp(bestScore * 0.75d + distinctness * 0.25d, 0d, 1d);
        return new TempoAnalysisResult(
            Math.Round(bpm, 2),
            absoluteOffsetSeconds + firstOnset / (double)EnvelopeRate,
            confidence);
    }

    private static double ResolveHalfDoubleTempo(double? previousBpm, double candidateBpm)
    {
        if (previousBpm is null)
        {
            return candidateBpm;
        }

        var previous = previousBpm.Value;
        if (Math.Abs(candidateBpm * 2d - previous) / previous <= 0.04d &&
            candidateBpm * 2d <= MaximumBpm)
        {
            return candidateBpm * 2d;
        }
        if (Math.Abs(candidateBpm / 2d - previous) / previous <= 0.04d &&
            candidateBpm / 2d >= MinimumBpm)
        {
            return candidateBpm / 2d;
        }
        return candidateBpm;
    }

    private static IReadOnlyList<TempoSegment> MergeStableWindows(
        IReadOnlyList<TempoSegment> windows)
    {
        var result = new List<TempoSegment>();
        foreach (var window in windows)
        {
            if (result.Count == 0)
            {
                result.Add(window with { StartSeconds = 0d });
                continue;
            }

            var previous = result[^1];
            var difference = Math.Abs(previous.Bpm - window.Bpm) /
                             Math.Max(previous.Bpm, window.Bpm);
            if (difference <= 0.025d)
            {
                var totalWeight = Math.Max(0.05d, previous.Confidence) +
                                  Math.Max(0.05d, window.Confidence);
                var bpm = ((previous.Bpm * Math.Max(0.05d, previous.Confidence)) +
                           (window.Bpm * Math.Max(0.05d, window.Confidence))) /
                          totalWeight;
                result[^1] = previous with
                {
                    Bpm = Math.Round(bpm, 2),
                    Confidence = Math.Max(previous.Confidence, window.Confidence)
                };
                continue;
            }

            result.Add(window);
        }
        return result;
    }

    private static int? EstimateOnsetInterval(
        IReadOnlyList<double> envelope,
        int minimumLag,
        int maximumLag)
    {
        var maximum = envelope.Max();
        var threshold = maximum * 0.42d;
        var refractory = Math.Max(1, minimumLag / 2);
        var peaks = new List<int>();
        var lastPeak = -refractory;
        for (var index = 1; index < envelope.Count - 1; index++)
        {
            if (envelope[index] < threshold ||
                envelope[index] < envelope[index - 1] ||
                envelope[index] < envelope[index + 1] ||
                index - lastPeak < refractory)
            {
                continue;
            }
            peaks.Add(index);
            lastPeak = index;
        }

        if (peaks.Count < 4)
        {
            return null;
        }

        var intervals = peaks.Zip(peaks.Skip(1), (left, right) => right - left)
            .Where(interval => interval >= minimumLag && interval <= maximumLag)
            .Order()
            .ToArray();
        if (intervals.Length < 3)
        {
            return null;
        }
        return intervals[intervals.Length / 2];
    }

    private static double[] BuildOnsetEnvelope(
        AudioFileReader reader,
        CancellationToken cancellationToken)
    {
        var channels = reader.WaveFormat.Channels;
        var framesPerEnvelope = Math.Max(1, reader.WaveFormat.SampleRate / EnvelopeRate);
        var buffer = new float[32_768 - 32_768 % channels];
        var energies = new List<double>();
        double energy = 0d;
        var frames = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = ((ISampleProvider)reader).Read(buffer.AsSpan());
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
                frames++;
                if (frames < framesPerEnvelope)
                {
                    continue;
                }

                energies.Add(Math.Sqrt(energy / frames));
                energy = 0d;
                frames = 0;
            }
        }

        if (frames > 0)
        {
            energies.Add(Math.Sqrt(energy / frames));
        }

        var onset = new double[energies.Count];
        var previous = energies.Count > 0 ? energies[0] : 0d;
        for (var index = 1; index < energies.Count; index++)
        {
            var smoothedPrevious = previous * 0.92d + energies[index - 1] * 0.08d;
            onset[index] = Math.Max(0d, energies[index] - smoothedPrevious);
            previous = smoothedPrevious;
        }
        return onset;
    }
}
