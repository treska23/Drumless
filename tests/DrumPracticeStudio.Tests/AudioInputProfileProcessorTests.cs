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
        using var processor = new AudioInputProfileProcessor(
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
            using var processor = new AudioInputProfileProcessor(48_000, profile);
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
        using var processor = new AudioInputProfileProcessor(
            48_000,
            AudioInputProfileKind.GuitarDrive);

        var result = 0f;
        for (var index = 0; index < 512; index++)
        {
            result = processor.Process(0.35f);
        }

        Assert.AreNotEqual(0.35f, result, 0.01f);
    }

    [TestMethod]
    public void CustomChain_BypassPreservesSignalAndEnabledChainChangesIt()
    {
        using var processor = new AudioInputProfileProcessor(
            48_000,
            AudioInputProfileKind.Clean);
        var effects = new[]
        {
            AudioEffectSlotSetting.Create(AudioEffectKind.Saturation, amount: 0.9d),
            AudioEffectSlotSetting.Create(AudioEffectKind.Compressor, amount: 0.6d)
        };
        processor.SetEffects(effects, bypassed: true);
        Assert.AreEqual(0.32f, processor.Process(0.32f), 0.00001f);

        processor.SetEffects(effects, bypassed: false);
        var processed = 0f;
        for (var index = 0; index < 256; index++)
        {
            processed = processor.Process(0.32f);
        }
        Assert.AreNotEqual(0.32f, processed, 0.01f);
    }

    [TestMethod]
    public void PresetsNeverExceedFourSlots()
    {
        foreach (var profile in Enum.GetValues<AudioInputProfileKind>())
        {
            Assert.IsTrue(
                AudioInputEffectPresetCatalog.Create(profile).Count <=
                AudioEffectCatalog.MaximumSlots);
        }
    }
}
