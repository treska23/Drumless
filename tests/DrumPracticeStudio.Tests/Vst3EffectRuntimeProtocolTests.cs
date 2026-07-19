using System.Text.Json;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class Vst3EffectRuntimeProtocolTests
{
    [TestMethod]
    public void ReadyMessage_PreservesEditorAvailability()
    {
        var json = JsonSerializer.Serialize(new Vst3EffectRuntimeReady(
            true,
            128,
            "Preparado",
            HasEditor: true));

        var restored = JsonSerializer.Deserialize<Vst3EffectRuntimeReady>(json);

        Assert.IsNotNull(restored);
        Assert.IsTrue(restored.HasEditor);
        Assert.AreEqual((uint)128, restored.LatencySamples);
    }

    [TestMethod]
    public void EditorCommands_AreReservedOutsidePositiveAudioFrameRange()
    {
        Assert.IsLessThan(0, Vst3EffectRuntimeProtocol.OpenEditorCommand);
        Assert.IsLessThan(0, Vst3EffectRuntimeProtocol.CloseEditorCommand);
    }

    [TestMethod]
    public void Configuration_PreservesAutomaticStatePath()
    {
        var configuration = new Vst3EffectRuntimeConfiguration(
            CreateMissingReference("State Test"),
            48_000,
            64,
            "ready.json",
            "effect.log",
            "effect-state.vstpreset");

        var restored = JsonSerializer.Deserialize<Vst3EffectRuntimeConfiguration>(
            JsonSerializer.Serialize(configuration));

        Assert.IsNotNull(restored);
        Assert.AreEqual("effect-state.vstpreset", restored.StatePath);
    }

    [TestMethod]
    public void Execute_InvalidModuleReportsFailureWithoutCrashingCaller()
    {
        using var temporary = new TemporaryDirectory();
        var configurationPath = temporary.Combine("effect.json");
        var readyPath = temporary.Combine("ready.json");
        var diagnosticPath = temporary.Combine("effect.log");
        var configuration = new Vst3EffectRuntimeConfiguration(
            CreateMissingReference("Missing Effect", temporary.Combine("missing.vst3")),
            48_000,
            64,
            readyPath,
            diagnosticPath);
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(configuration));

        var exitCode = Vst3EffectRuntimeProtocol.Execute(configurationPath);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(File.Exists(readyPath));
        var ready = JsonSerializer.Deserialize<Vst3EffectRuntimeReady>(
            File.ReadAllText(readyPath));
        Assert.IsNotNull(ready);
        Assert.IsFalse(ready.Ready);
        StringAssert.Contains(ready.Message, "missing");
        Assert.IsTrue(File.Exists(diagnosticPath));
    }

    private static Vst3EffectReference CreateMissingReference(
        string name,
        string modulePath = "missing.vst3") =>
        new(
            modulePath,
            "Missing",
            "00112233445566778899AABBCCDDEEFF",
            "Audio Module Class",
            name,
            "Test",
            "1",
            "3.7",
            "Fx");
}
