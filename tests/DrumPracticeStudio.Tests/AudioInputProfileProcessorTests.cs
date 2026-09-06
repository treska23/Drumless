using DrumPracticeStudio.Audio;
using DrumPracticeStudio.Models;
using System.Reflection;

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
    public void EveryProfile_LeavesSignalCleanWithoutAutomaticPlugins()
    {
        foreach (var profile in Enum.GetValues<AudioInputProfileKind>()
                     .Where(profile => profile != AudioInputProfileKind.Clean))
        {
            using var processor = new AudioInputProfileProcessor(48_000, profile);
            for (var sampleIndex = 0; sampleIndex < 10_000; sampleIndex++)
            {
                var source = (float)Math.Sin(sampleIndex * 0.071d) * 0.82f;
                var result = processor.Process(source);

                Assert.AreEqual(source, result, 0.00001f);
            }
        }
    }

    [TestMethod]
    public void GuitarDriveProfile_DoesNotApplyBuiltInDistortion()
    {
        using var processor = new AudioInputProfileProcessor(
            48_000,
            AudioInputProfileKind.GuitarDrive);

        var result = 0f;
        for (var index = 0; index < 512; index++)
        {
            result = processor.Process(0.35f);
        }

        Assert.AreEqual(0.35f, result, 0.00001f);
    }

    [TestMethod]
    public void RemovedInternalChain_IsIgnored()
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
        Assert.AreEqual(0.32f, processed, 0.00001f);
    }

    [TestMethod]
    public void ProfilesNeverCreateDefaultEffects()
    {
        foreach (var profile in Enum.GetValues<AudioInputProfileKind>())
        {
            Assert.AreEqual(0, AudioInputEffectPresetCatalog.Create(profile).Count);
        }
    }

    [TestMethod]
    public void ExternalSignature_ChangesWhenConfiguredParametersChange()
    {
        using var processor = new AudioInputProfileProcessor(
            48_000,
            AudioInputProfileKind.Clean);
        var pluginPath = Path.Combine(
            Path.GetTempPath(),
            $"missing-{Guid.NewGuid():N}.vst3");
        var reference = new Vst3EffectReference(
            pluginPath,
            "Missing",
            "00112233445566778899AABBCCDDEEFF",
            "Audio Module Class",
            "Missing FX",
            "Vendor",
            "1",
            "3.7",
            "Fx",
            ParameterSettings:
            [
                new Vst3ParameterSetting(17, "Drive", 0.25d)
            ]);
        var slot = new AudioEffectSlotSetting(
            "slot-1",
            AudioEffectKind.ExternalVst3,
            ExternalVst3: reference);

        processor.SetEffects([slot], bypassed: false);
        var firstSignature = GetExternalSignature(processor);

        processor.SetEffects(
            [slot with
            {
                ExternalVst3 = reference with
                {
                    ParameterSettings =
                    [
                        new Vst3ParameterSetting(17, "Drive", 0.75d)
                    ]
                }
            }],
            bypassed: false);
        var secondSignature = GetExternalSignature(processor);

        Assert.AreNotEqual(firstSignature, secondSignature);
    }

    private static string GetExternalSignature(AudioInputProfileProcessor processor) =>
        (string)(typeof(AudioInputProfileProcessor)
            .GetField("_externalSignature", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(processor)
            ?? throw new AssertFailedException("No se encontró la firma externa del procesador."));
}
