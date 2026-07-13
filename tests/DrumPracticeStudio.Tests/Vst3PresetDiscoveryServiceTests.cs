using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using NAudio.Vst3;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class Vst3PresetDiscoveryServiceTests
{
    private const string GrooveAgentClassId = "00112233445566778899AABBCCDDEEFF";

    [TestMethod]
    public void FindCompatiblePreset_IgnoresOtherPluginsAndCorruptFiles()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Combine("VST3 Presets");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "corrupt.vstpreset"), "not a preset");
        WritePreset(
            Path.Combine(root, "other.vstpreset"),
            "FFEEDDCCBBAA99887766554433221100");
        var expected = Path.Combine(root, "Natural Studio Kit.vstpreset");
        WritePreset(expected, GrooveAgentClassId);
        var service = new Vst3PresetDiscoveryService([root]);

        var result = service.FindCompatiblePreset(CreateInstrument(GrooveAgentClassId));

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void FindCompatiblePreset_PrefersNamedKitOverEmptyPreset()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.Combine("VST3 Presets");
        Directory.CreateDirectory(root);
        WritePreset(Path.Combine(root, "Empty Init.vstpreset"), GrooveAgentClassId);
        var expected = Path.Combine(root, "Rock Kit.vstpreset");
        WritePreset(expected, GrooveAgentClassId);
        var service = new Vst3PresetDiscoveryService([root]);

        var result = service.FindCompatiblePreset(CreateInstrument(GrooveAgentClassId));

        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void FindCompatiblePreset_ChoosesBestKitAcrossAllStandardRoots()
    {
        using var temporary = new TemporaryDirectory();
        var firstRoot = temporary.Combine("User Presets");
        var secondRoot = temporary.Combine("Factory Presets");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);
        WritePreset(Path.Combine(firstRoot, "Empty Init.vstpreset"), GrooveAgentClassId);
        var expected = Path.Combine(secondRoot, "Natural Studio Kit.vstpreset");
        WritePreset(expected, GrooveAgentClassId);
        var service = new Vst3PresetDiscoveryService([firstRoot, secondRoot]);

        var result = service.FindCompatiblePreset(CreateInstrument(GrooveAgentClassId));

        Assert.AreEqual(expected, result);
    }

    private static void WritePreset(string path, string classId)
    {
        using var stream = File.Create(path);
        Vst3Preset.Write(stream, classId, [1, 2, 3], [4, 5, 6]);
    }

    private static Vst3InstrumentItem CreateInstrument(string classId) => new(
        new Vst3ModuleInfo(@"C:\VST3\Groove Agent SE.vst3", "Groove Agent SE"),
        new Vst3ClassInfo(
            classId,
            Vst3ClassInfo.AudioModuleCategory,
            "Groove Agent SE",
            "Steinberg Media Technologies GmbH",
            "1.0",
            "VST 3.7",
            "Instrument|Drum"));
}
