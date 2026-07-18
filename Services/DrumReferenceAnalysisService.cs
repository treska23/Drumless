using System.Security.Cryptography;
using System.Text;
using DrumPracticeStudio.Models;
using NAudio.Wave;

namespace DrumPracticeStudio.Services;

public sealed class DrumReferenceAnalysisService
{
    private const int EnvelopeRate = 500;

    public Task<DrumReferenceMap> AnalyzeAsync(
        string path,
        CancellationToken cancellationToken = default) => Task.Run(
        () => Analyze(path, cancellationToken),
        cancellationToken);

    public DrumReferenceMap Analyze(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var reader = new AudioFileReader(path);
        var channels = reader.WaveFormat.Channels;
        var framesPerEnvelope = Math.Max(1, reader.WaveFormat.SampleRate / EnvelopeRate);
        var buffer = new float[32_768 - (32_768 % channels)];
        var envelope = new List<double>();
        double energy = 0d;
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
                frames++;
                if (frames >= framesPerEnvelope)
                {
                    envelope.Add(Math.Sqrt(energy / frames));
                    energy = 0d;
                    frames = 0;
                }
            }
        }
        if (envelope.Count < EnvelopeRate * 2)
        {
            throw new InvalidDataException("La referencia es demasiado corta.");
        }

        var flux = new double[envelope.Count];
        var smoothed = envelope[0];
        for (var index = 1; index < envelope.Count; index++)
        {
            smoothed = (smoothed * 0.9d) + (envelope[index - 1] * 0.1d);
            flux[index] = Math.Max(0d, envelope[index] - smoothed);
        }
        var nonZero = flux.Where(value => value > 0d).Order().ToArray();
        if (nonZero.Length < 8)
        {
            throw new InvalidDataException("No se detectaron transitorios de batería suficientes.");
        }
        var median = nonZero[nonZero.Length / 2];
        var deviations = nonZero
            .Select(value => Math.Abs(value - median))
            .Order()
            .ToArray();
        var mad = deviations[deviations.Length / 2];
        var threshold = Math.Max(
            nonZero.Max() * 0.08d,
            median + (mad * 5d));
        var refractory = (int)(EnvelopeRate * 0.055d);
        var hits = new List<double>();
        var strengths = new List<double>();
        var last = -refractory;
        for (var index = 1; index < flux.Length - 1; index++)
        {
            if (flux[index] < threshold ||
                flux[index] < flux[index - 1] ||
                flux[index] < flux[index + 1] ||
                index - last < refractory)
            {
                continue;
            }
            hits.Add(index / (double)EnvelopeRate);
            strengths.Add(flux[index] / Math.Max(threshold, 1e-12d));
            last = index;
        }
        if (hits.Count < 4)
        {
            throw new InvalidDataException(
                "No se detectaron suficientes golpes; usa una pista de batería más aislada.");
        }

        var confidence = Math.Clamp(
            (hits.Count / Math.Max(1d, reader.TotalTime.TotalSeconds) / 4d) * 0.45d +
            Math.Min(1d, strengths.Average() / 3d) * 0.55d,
            0d,
            1d);
        var versionPayload = $"{Path.GetFullPath(path)}|{string.Join(",", hits.Select(hit => hit.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)))}";
        var version = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(versionPayload)))[..16];
        return DrumReferenceMap.Normalize(new DrumReferenceMap(
            version,
            Path.GetFullPath(path),
            DateTimeOffset.UtcNow,
            confidence,
            hits));
    }
}
