using DrumPracticeStudio.Services;
using NAudio.Wave;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class SongStructureAnalysisServiceTests
{
    [TestMethod]
    public void Analyze_ProducesOrderedSectionsCoveringTheCompleteTrack()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("sections.wav");
        WriteSectionedTrack(path);

        var result = new SongStructureAnalysisService().Analyze(path, tempo: null);

        Assert.IsTrue(result.Sections.Count >= 2);
        Assert.AreEqual(0d, result.Sections[0].StartSeconds, 0.01d);
        Assert.AreEqual(36d, result.Sections[^1].EndSeconds, 0.05d);
        for (var index = 1; index < result.Sections.Count; index++)
        {
            Assert.IsTrue(
                result.Sections[index].StartSeconds >= result.Sections[index - 1].EndSeconds);
        }
    }

    private static void WriteSectionedTrack(string path)
    {
        const int sampleRate = 8_000;
        const int durationSeconds = 36;
        var samples = new float[sampleRate * durationSeconds];
        for (var index = 0; index < samples.Length; index++)
        {
            var seconds = index / (double)sampleRate;
            var middle = seconds is >= 12d and < 24d;
            var frequency = middle ? 880d : 220d;
            var amplitude = middle ? 0.72d : 0.18d;
            var pulse = middle && index % (sampleRate / 4) < 100 ? 0.2d : 0d;
            samples[index] = (float)(
                Math.Sin(2d * Math.PI * frequency * seconds) * amplitude + pulse);
        }
        using var writer = new WaveFileWriter(
            path,
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1));
        writer.WriteSamples(samples, 0, samples.Length);
    }
}
