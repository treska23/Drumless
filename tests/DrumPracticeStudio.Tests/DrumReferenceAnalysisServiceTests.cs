using DrumPracticeStudio.Services;
using NAudio.Wave;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class DrumReferenceAnalysisServiceTests
{
    [TestMethod]
    public void Analyze_DetectsSyntheticDrumTransients()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("drums.wav");
        WriteHits(path, [0.5d, 1d, 1.5d, 2d, 2.75d, 3.5d]);

        var result = new DrumReferenceAnalysisService().Analyze(path);

        Assert.AreEqual(6, result.HitTimesSeconds.Count);
        Assert.AreEqual(0.5d, result.HitTimesSeconds[0], 0.01d);
        Assert.AreEqual(3.5d, result.HitTimesSeconds[^1], 0.01d);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Version));
    }

    private static void WriteHits(string path, IReadOnlyList<double> hits)
    {
        const int sampleRate = 48_000;
        var samples = new float[sampleRate * 4];
        foreach (var hit in hits)
        {
            var start = (int)(hit * sampleRate);
            for (var offset = 0; offset < sampleRate / 40; offset++)
            {
                var envelope = Math.Exp(-offset / (sampleRate * 0.006d));
                samples[start + offset] = (float)(
                    Math.Sin(offset * 2d * Math.PI * 90d / sampleRate) * envelope);
            }
        }
        using var writer = new WaveFileWriter(
            path,
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }
}
