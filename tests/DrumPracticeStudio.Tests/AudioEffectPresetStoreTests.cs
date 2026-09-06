using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using NAudio.Vst3;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class AudioEffectPresetStoreTests
{
    [TestMethod]
    public void SaveAndLoad_RoundTripsOrderConfigurationAndEmbeddedPluginStates()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("voice.dpsfx");
        var stateDirectory = temporary.Combine("states");
        var store = new AudioEffectPresetStore(stateDirectory);
        const string compressorId = "compressor-slot";
        const string reverbId = "reverb-slot";
        const string compressorClassId = "00112233445566778899AABBCCDDEEFF";
        const string reverbClassId = "FFEEDDCCBBAA99887766554433221100";
        var compressorState = CreatePreset(compressorClassId, [1, 2, 3], [4, 5]);
        var reverbState = CreatePreset(reverbClassId, [6, 7, 8], []);
        store.Save(path, new AudioEffectChainPreset(
            "Voz personal",
            [
                AudioEffectSlotSetting.Create(AudioEffectKind.Compressor, 0.7d, 0.8d),
                new AudioEffectSlotSetting(
                    compressorId,
                    AudioEffectKind.ExternalVst3,
                    IsEnabled: false,
                    Amount: 0.7d,
                    Mix: 0.8d,
                    ExternalVst3: new Vst3EffectReference(
                        @"C:\Fx\Test.vst3",
                        "Test",
                        compressorClassId,
                        "Audio Module Class",
                        "Compressor FX",
                        "Vendor",
                        "1",
                        "3.7",
                        "Fx|Dynamics",
                        ParameterSettings:
                        [
                            new Vst3ParameterSetting(42, "Threshold", 0.25d)
                        ])),
                new AudioEffectSlotSetting(
                    reverbId,
                    AudioEffectKind.ExternalVst3,
                    Mix: 0.35d,
                    ExternalVst3: new Vst3EffectReference(
                        @"C:\Fx\Reverb.vst3",
                        "Reverb",
                        reverbClassId,
                        "Audio Module Class",
                        "Reverb FX",
                        "Vendor",
                        "2",
                        "3.7",
                        "Fx|Reverb"))
            ],
            EffectsBypassed: true,
            PluginStates: new Dictionary<string, byte[]>
            {
                [compressorId] = compressorState,
                [reverbId] = reverbState
            }));

        var loaded = store.Load(path);

        Assert.AreEqual("Voz personal", loaded.Name);
        Assert.IsTrue(loaded.EffectsBypassed);
        Assert.AreEqual(2, loaded.Effects.Count);
        Assert.AreEqual("Compressor FX", loaded.Effects[0].ExternalVst3?.Name);
        Assert.AreEqual("Reverb FX", loaded.Effects[1].ExternalVst3?.Name);
        Assert.AreNotEqual(compressorId, loaded.Effects[0].Id);
        Assert.AreNotEqual(reverbId, loaded.Effects[1].Id);
        Assert.AreNotEqual(loaded.Effects[0].Id, loaded.Effects[1].Id);
        Assert.AreEqual(AudioEffectKind.ExternalVst3, loaded.Effects[0].Kind);
        Assert.IsFalse(loaded.Effects[0].IsEnabled);
        Assert.AreEqual(0.8d, loaded.Effects[0].Mix);
        Assert.AreEqual(42u, loaded.Effects[0].ExternalVst3?.EffectiveParameterSettings[0].Id);
        Assert.AreEqual(0.35d, loaded.Effects[1].Mix);

        Assert.IsNotNull(loaded.PluginStates);
        CollectionAssert.AreEqual(
            compressorState,
            loaded.PluginStates[loaded.Effects[0].Id]);
        CollectionAssert.AreEqual(
            reverbState,
            loaded.PluginStates[loaded.Effects[1].Id]);
        AssertMaterializedState(
            loaded.Effects[0],
            stateDirectory,
            compressorState,
            compressorClassId);
        AssertMaterializedState(
            loaded.Effects[1],
            stateDirectory,
            reverbState,
            reverbClassId);

        var loadedAgain = store.Load(path);
        Assert.AreNotEqual(loaded.Effects[0].Id, loadedAgain.Effects[0].Id);
        Assert.AreNotEqual(loaded.Effects[1].Id, loadedAgain.Effects[1].Id);
        CollectionAssert.AreEqual(
            compressorState,
            loadedAgain.PluginStates![loadedAgain.Effects[0].Id]);

        var json = File.ReadAllText(path);
        StringAssert.Contains(json, "\"schemaVersion\": 2");
        StringAssert.Contains(json, "\"pluginStates\"");
        StringAssert.Contains(json, Convert.ToBase64String(compressorState));
    }

    [TestMethod]
    public void Load_AcceptsLegacySchemaVersionOne()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("legacy.dpsfx");
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 1,
              "name": "Cadena antigua",
              "effects": [
                {
                  "id": "legacy-slot",
                  "kind": "externalVst3",
                  "isEnabled": true,
                  "amount": 0.5,
                  "mix": 0.6,
                  "externalVst3": {
                    "modulePath": "C:\\Fx\\Legacy.vst3",
                    "moduleName": "Legacy",
                    "classId": "00112233445566778899AABBCCDDEEFF",
                    "category": "Audio Module Class",
                    "name": "Legacy FX",
                    "vendor": "Vendor",
                    "version": "1",
                    "sdkVersion": "3.7",
                    "subCategories": "Fx"
                  }
                }
              ],
              "effectsBypassed": false
            }
            """);

        var loaded = new AudioEffectPresetStore(temporary.Combine("states")).Load(path);

        Assert.AreEqual("Cadena antigua", loaded.Name);
        Assert.AreEqual(1, loaded.Effects.Count);
        Assert.AreEqual("Legacy FX", loaded.Effects[0].ExternalVst3?.Name);
        Assert.AreNotEqual("legacy-slot", loaded.Effects[0].Id);
        Assert.IsNull(loaded.PluginStates);
    }

    [TestMethod]
    public void Load_LegacyPresetRecoversItsAutomaticStateOnTheSameComputer()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("legacy-with-state.dpsfx");
        var stateDirectory = temporary.Combine("states");
        const string oldSlotId = "legacy-state-slot";
        const string classId = "00112233445566778899AABBCCDDEEFF";
        var effect = CreateEffect(oldSlotId, classId, "Legacy FX");
        var state = CreatePreset(classId, [9, 8, 7], [6, 5]);
        Directory.CreateDirectory(stateDirectory);
        File.WriteAllBytes(
            Vst3EffectStateFiles.GetAutomaticPath(
                oldSlotId,
                effect.ExternalVst3!,
                stateDirectory),
            state);
        File.WriteAllText(
            path,
            $$"""
            {
              "schemaVersion": 1,
              "name": "Cadena recuperable",
              "effects": [
                {
                  "id": "{{oldSlotId}}",
                  "kind": "externalVst3",
                  "isEnabled": true,
                  "amount": 0.5,
                  "mix": 0.75,
                  "externalVst3": {
                    "modulePath": "C:\\Fx\\Legacy FX.vst3",
                    "moduleName": "Legacy FX",
                    "classId": "{{classId}}",
                    "category": "Audio Module Class",
                    "name": "Legacy FX",
                    "vendor": "Vendor",
                    "version": "1",
                    "sdkVersion": "3.7",
                    "subCategories": "Fx"
                  }
                }
              ],
              "effectsBypassed": false
            }
            """);

        var loaded = new AudioEffectPresetStore(stateDirectory).Load(path);

        Assert.AreEqual(1, loaded.Effects.Count);
        Assert.AreNotEqual(oldSlotId, loaded.Effects[0].Id);
        Assert.IsNotNull(loaded.PluginStates);
        CollectionAssert.AreEqual(state, loaded.PluginStates[loaded.Effects[0].Id]);
        CollectionAssert.AreEqual(
            state,
            File.ReadAllBytes(loaded.Effects[0].ExternalVst3!.PresetPath!));
    }

    [TestMethod]
    public void Load_RejectsEmbeddedStateFromAnotherPlugin()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("mismatch.dpsfx");
        const string expectedClassId = "00112233445566778899AABBCCDDEEFF";
        const string otherClassId = "FFEEDDCCBBAA99887766554433221100";
        var store = new AudioEffectPresetStore(temporary.Combine("states"));
        store.Save(path, new AudioEffectChainPreset(
            "Cadena",
            [CreateEffect("slot", expectedClassId, "FX")],
            PluginStates: new Dictionary<string, byte[]>
            {
                ["slot"] = CreatePreset(expectedClassId, [1], [])
            }));
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace(
                expectedClassId,
                otherClassId,
                StringComparison.Ordinal));

        var exception = Assert.ThrowsExactly<InvalidDataException>(() => store.Load(path));

        StringAssert.Contains(exception.Message, "pertenece al plugin");
        Assert.IsFalse(Directory.Exists(temporary.Combine("states")));
    }

    private static AudioEffectSlotSetting CreateEffect(
        string id,
        string classId,
        string name) => new(
        id,
        AudioEffectKind.ExternalVst3,
        ExternalVst3: new Vst3EffectReference(
            $@"C:\Fx\{name}.vst3",
            name,
            classId,
            "Audio Module Class",
            name,
            "Vendor",
            "1",
            "3.7",
            "Fx"));

    private static byte[] CreatePreset(
        string classId,
        byte[] componentState,
        byte[] controllerState)
    {
        using var stream = new MemoryStream();
        Vst3Preset.Write(stream, classId, componentState, controllerState);
        return stream.ToArray();
    }

    private static void AssertMaterializedState(
        AudioEffectSlotSetting effect,
        string stateDirectory,
        byte[] expectedState,
        string expectedClassId)
    {
        var statePath = effect.ExternalVst3?.PresetPath;
        Assert.IsNotNull(statePath);
        Assert.AreEqual(
            Path.GetFullPath(stateDirectory),
            Path.GetDirectoryName(statePath));
        CollectionAssert.AreEqual(expectedState, File.ReadAllBytes(statePath));
        Assert.AreEqual(expectedClassId, Vst3Preset.ReadClassId(statePath));
    }
}
