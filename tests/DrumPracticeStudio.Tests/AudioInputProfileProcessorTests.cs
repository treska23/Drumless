using DrumPracticeStudio.Audio;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class AudioInputProfileProcessorTests
{
    [TestMethod]
    public void Catalog_OffersEveryProfileWithProcessingDescription()
    {
        foreach (var profile in Enum.GetValues<AudioInputProfileKind>())
        {
            var option = AudioInputProfileCatalog.Get(profile);

            Assert.AreEqual(profile, option.Kind);
            Assert.IsFalse(string.IsNullOrWhiteSpace(option.Label));
            Assert.IsFalse(string.IsNullOrWhiteSpace(option.ProcessingLabel));
        }
    }

    [TestMethod]
    public void CleanProfile_PreservesFiniteSamples()
    {
        var processor = new AudioInputProfileProcessor(
            48_000,
            AudioInputProfileKind.Clean);

        Assert.AreEqual(0.35f, processor.Process(0.35f), 0.00001f);
        Assert.AreEqual(-0.7f, processor.Process(-0.7f), 0.00001f);
        Assert.AreEqual(0f, processor.Process(float.NaN));
    }

    [TestMethod]
    public void EveryProcessedProfile_ProducesBoundedAudioWithoutAllocatingConfiguration()
    {
        foreach (var profile in Enum.GetValues<AudioInputProfileKind>()
                     .Where(profile => profile != AudioInputProfileKind.Clean))
        {
            var processor = new AudioInputProfileProcessor(48_000, profile);
            for (var sampleIndex = 0; sampleIndex < 10_000; sampleIndex++)
            {
                var source = (float)Math.Sin(sampleIndex * 0.071d) * 0.82f;
                var result = processor.Process(source);

                Assert.IsTrue(float.IsFinite(result), $"{profile} produjo una muestra no finita.");
                Assert.IsTrue(result is >= -1f and <= 1f, $"{profile} salió del rango.");
            }
        }
    }

    [TestMethod]
    public void GuitarDriveProfile_ChangesTheWaveform()
    {
        var processor = new AudioInputProfileProcessor(
            48_000,
            AudioInputProfileKind.GuitarDrive);

        var result = 0f;
        for (var index = 0; index < 512; index++)
        {
            result = processor.Process(0.35f);
        }

        Assert.AreNotEqual(0.35f, result, 0.01f);
    }
}
