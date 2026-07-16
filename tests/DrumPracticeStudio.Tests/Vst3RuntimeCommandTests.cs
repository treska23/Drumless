using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class Vst3RuntimeCommandTests
{
    [DataRow("NoteOn")]
    [DataRow("NoteOff")]
    [DataRow("ControlChange")]
    [DataRow("Panic")]
    [TestMethod]
    public void RealtimeMidi_DoesNotUseTheUiThreadOrPerHitLog(string type)
    {
        var command = new Vst3RuntimeCommand(type);

        Assert.IsTrue(Vst3RuntimeProtocol.IsRealtimeMidi(command));
        Assert.IsFalse(Vst3RuntimeProtocol.RequiresUiThread(command));
    }

    [DataRow("OpenEditor")]
    [DataRow("CloseEditor")]
    [DataRow("LoadPreset")]
    [DataRow("SavePreset")]
    [TestMethod]
    public void EditorAndPresetCommands_StayOnTheUiThread(string type)
    {
        var command = new Vst3RuntimeCommand(type);

        Assert.IsFalse(Vst3RuntimeProtocol.IsRealtimeMidi(command));
        Assert.IsTrue(Vst3RuntimeProtocol.RequiresUiThread(command));
    }

    [DataRow("StartRecording")]
    [DataRow("StopRecording")]
    [TestMethod]
    public void RecordingCommands_RunOnThePipeWorkerAndAreNotRealtimeMidi(string type)
    {
        var command = new Vst3RuntimeCommand(type);

        Assert.IsFalse(Vst3RuntimeProtocol.IsRealtimeMidi(command));
        Assert.IsFalse(Vst3RuntimeProtocol.RequiresUiThread(command));
    }
}
