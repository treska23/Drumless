using DrumPracticeStudio.Services;
using NAudio.Wave;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class AudioFileMixServiceTests
{
    [TestMethod]
    public async Task MixAsync_CombinesMainEngineAndIsolatedVstRecordings()
    {
        using var temporary = new TemporaryDirectory();
        var main = temporary.Combine("main.wav");
        var vst = temporary.Combine("vst.wav");
        var destination = temporary.Combine("final.wav");
        WriteConstant(main, 0.15f);
        WriteConstant(vst, 0.25f);

        await AudioFileMixService.MixAsync([main, vst], destination);

        using var reader = new AudioFileReader(destination);
        var samples = new float[128];
        ((ISampleProvider)reader).Read(samples);
        foreach (var sample in samples)
        {
            Assert.AreEqual(0.40f, sample, 0.001f);
        }
    }

    private static void WriteConstant(string path, float value)
    {
        using var writer = new WaveFileWriter(
            path,
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2));
        var samples = Enumerable.Repeat(value, 4_800).ToArray();
        writer.WriteSamples(samples, 0, samples.Length);
    }
}
