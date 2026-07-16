using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using NAudio.Wave;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class StemAudioMixerTests
{
    [TestMethod]
    public async Task MixAsync_CombinesOnlyTheRequestedStemFiles()
    {
        using var temporary = new TemporaryDirectory();
        WriteConstantWave(Path.Combine(temporary.Path, "drums.wav"), 0.10f);
        WriteConstantWave(Path.Combine(temporary.Path, "bass.wav"), 0.20f);
        WriteConstantWave(Path.Combine(temporary.Path, "vocals.wav"), 0.30f);
        WriteConstantWave(Path.Combine(temporary.Path, "other.wav"), 0.40f);
        var destination = Path.Combine(temporary.Path, "mix.wav");

        await StemAudioMixer.MixAsync(
            temporary.Path,
            StemSelection.Drums | StemSelection.Bass,
            destination);

        using var reader = new AudioFileReader(destination);
        var samples = new float[64];
        var read = ((ISampleProvider)reader).Read(samples.AsSpan());

        Assert.AreEqual(samples.Length, read);
        foreach (var sample in samples)
        {
            Assert.IsTrue(sample is >= 0.29f and <= 0.31f, $"Muestra inesperada: {sample}");
        }
    }

    [TestMethod]
    public async Task MixAsync_FailsWhenDemucsDidNotProduceARequestedStem()
    {
        using var temporary = new TemporaryDirectory();
        WriteConstantWave(Path.Combine(temporary.Path, "drums.wav"), 0.10f);

        var exception = await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            StemAudioMixer.MixAsync(
                temporary.Path,
                StemSelection.Drums | StemSelection.Vocals,
                Path.Combine(temporary.Path, "mix.wav")));

        StringAssert.Contains(exception.Message, "vocals.wav");
    }

    private static void WriteConstantWave(string path, float value)
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        using var writer = new WaveFileWriter(path, format);
        var samples = Enumerable.Repeat(value, 4_800).ToArray();
        writer.WriteSamples(samples, 0, samples.Length);
    }
}
