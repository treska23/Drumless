using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class AudioEffectPresetStoreTests
{
    [TestMethod]
    public void SaveAndLoad_RoundTripsOrderBypassAndExternalReference()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("voice.dpsfx");
        var store = new AudioEffectPresetStore();
        store.Save(path, new AudioEffectChainPreset(
            "Voz personal",
            [
                AudioEffectSlotSetting.Create(AudioEffectKind.Compressor, 0.7d, 0.8d),
                AudioEffectSlotSetting.Create(
                    AudioEffectKind.ExternalVst3,
                    externalVst3: new Vst3EffectReference(
                        @"C:\Fx\Test.vst3",
                        "Test",
                        "00112233445566778899AABBCCDDEEFF",
                        "Audio Module Class",
                        "Test FX",
                        "Vendor",
                        "1",
                        "3.7",
                        "Fx"))
            ],
            EffectsBypassed: true));

        var loaded = store.Load(path);

        Assert.AreEqual("Voz personal", loaded.Name);
        Assert.IsTrue(loaded.EffectsBypassed);
        Assert.AreEqual(1, loaded.Effects.Count);
        Assert.AreEqual(AudioEffectKind.ExternalVst3, loaded.Effects[0].Kind);
        Assert.AreEqual("Test FX", loaded.Effects[0].ExternalVst3?.Name);
    }
}
