using DrumPracticeStudio.Services;
using NAudio.Wave;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class TempoAnalysisServiceTests
{
    [TestMethod]
    public void Analyze_DetectsSynthetic120BpmAndFirstBeat()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("click-120.wav");
        WriteClickTrack(path, bpm: 120d, firstBeatSeconds: 0.25d, durationSeconds: 18d);

        var result = new TempoAnalysisService().Analyze(path);

        Assert.AreEqual(120d, result.Bpm, 1.5d);
        Assert.AreEqual(0.25d, result.FirstBeatSeconds, 0.035d);
        Assert.IsTrue(result.Confidence > 0.45d, $"Confianza inesperada: {result.Confidence}");
    }

    private static void WriteClickTrack(
        string path,
        double bpm,
        double firstBeatSeconds,
        double durationSeconds)
    {
        const int sampleRate = 48_000;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        using var writer = new WaveFileWriter(path, format);
        var total = (int)(durationSeconds * sampleRate);
        var samples = new float[total];
        var beatFrames = (int)Math.Round(60d / bpm * sampleRate);
        var first = (int)Math.Round(firstBeatSeconds * sampleRate);
        for (var beat = first; beat < total; beat += beatFrames)
        {
            for (var offset = 0; offset < sampleRate / 50 && beat + offset < total; offset++)
            {
                var envelope = Math.Exp(-offset / (sampleRate * 0.004d));
                samples[beat + offset] = (float)(Math.Sin(offset * 2d * Math.PI * 1_500d / sampleRate) * envelope);
            }
        }
        writer.WriteSamples(samples, 0, samples.Length);
    }
}
