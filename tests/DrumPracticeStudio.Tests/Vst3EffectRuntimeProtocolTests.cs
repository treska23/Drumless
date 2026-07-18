using System.Text.Json;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class Vst3EffectRuntimeProtocolTests
{
    [TestMethod]
    public void Execute_InvalidModuleReportsFailureWithoutCrashingCaller()
    {
        using var temporary = new TemporaryDirectory();
        var configurationPath = temporary.Combine("effect.json");
        var readyPath = temporary.Combine("ready.json");
        var diagnosticPath = temporary.Combine("effect.log");
        var configuration = new Vst3EffectRuntimeConfiguration(
            new Vst3EffectReference(
                temporary.Combine("missing.vst3"),
                "Missing",
                "00112233445566778899AABBCCDDEEFF",
                "Audio Module Class",
                "Missing Effect",
                "Test",
                "1",
                "3.7",
                "Fx"),
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
}
