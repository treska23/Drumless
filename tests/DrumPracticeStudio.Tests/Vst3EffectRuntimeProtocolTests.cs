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
            "effect-state.vstpreset",
            "audio-pipe");

        var restored = JsonSerializer.Deserialize<Vst3EffectRuntimeConfiguration>(
            JsonSerializer.Serialize(configuration));

        Assert.IsNotNull(restored);
        Assert.AreEqual("effect-state.vstpreset", restored.StatePath);
        Assert.AreEqual("audio-pipe", restored.PipeName);
    }

    [TestMethod]
    public void ConvertHostInput_DownmixesStereoToMonoWithoutChangingLevel()
    {
        var stereo = new[] { 0.8f, 0.2f, -0.4f, 0.2f };
        var mono = new float[2];

        Vst3EffectRuntimeProtocol.ConvertHostInput(
            stereo,
            mono,
            frames: 2,
            pluginChannels: 1);

        CollectionAssert.AreEqual(new[] { 0.5f, -0.1f }, mono);
    }

    [TestMethod]
    public void ConvertPluginOutput_DuplicatesMonoIntoBothHostChannels()
    {
        var mono = new[] { 0.25f, -0.75f };
        var stereo = new float[4];

        Vst3EffectRuntimeProtocol.ConvertPluginOutput(
            mono,
            stereo,
            frames: 2,
            pluginChannels: 1);

        CollectionAssert.AreEqual(
            new[] { 0.25f, 0.25f, -0.75f, -0.75f },
            stereo);
    }

    [TestMethod]
    public void StereoConversions_PreserveEverySample()
    {
        var original = new[] { 0.1f, 0.2f, 0.3f, 0.4f };
        var pluginInput = new float[4];
        var hostOutput = new float[4];

        Vst3EffectRuntimeProtocol.ConvertHostInput(
            original,
            pluginInput,
            frames: 2,
            pluginChannels: 2);
        Vst3EffectRuntimeProtocol.ConvertPluginOutput(
            pluginInput,
            hostOutput,
            frames: 2,
            pluginChannels: 2);

        CollectionAssert.AreEqual(original, hostOutput);
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
