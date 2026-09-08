using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class RecordingFileStoreTests
{
    [TestMethod]
    public void Publish_DoesNotOverwriteFilesCreatedAfterRecordingStarted()
    {
        using var temp = new TemporaryDirectory();
        var rendered = Path.Combine(temp.Path, "rendered.wav");
        var destination = Path.Combine(temp.Path, "take.wav");
        File.WriteAllText(rendered, "new recording");
        File.WriteAllText(destination, "existing recording");
        File.WriteAllText(Path.Combine(temp.Path, "take-2.wav"), "another recording");

        var published = RecordingFileStore.Publish(rendered, destination);

        Assert.AreEqual(Path.Combine(temp.Path, "take-3.wav"), published);
        Assert.AreEqual("new recording", File.ReadAllText(published));
        Assert.AreEqual("existing recording", File.ReadAllText(destination));
        Assert.AreEqual("another recording", File.ReadAllText(Path.Combine(temp.Path, "take-2.wav")));
        Assert.AreEqual("new recording", File.ReadAllText(rendered));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }

    [TestMethod]
    public void Publish_WhenDestinationIsUnavailable_PreservesRenderedRecording()
    {
        using var temp = new TemporaryDirectory();
        var rendered = Path.Combine(temp.Path, "rendered.wav");
        var unavailableDirectory = Path.Combine(temp.Path, "blocked");
        File.WriteAllText(rendered, "recoverable recording");
        File.WriteAllText(unavailableDirectory, "file prevents directory creation");

        Assert.Throws<IOException>(() => RecordingFileStore.Publish(rendered,
            Path.Combine(unavailableDirectory, "take.wav")));

        Assert.AreEqual("recoverable recording", File.ReadAllText(rendered));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "*.tmp").Length);
    }
}
