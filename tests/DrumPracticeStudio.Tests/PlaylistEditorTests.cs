using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class PlaylistEditorTests
{
    [TestMethod]
    public void EditOperations_PreserveOrderAndNeverDeleteAudio()
    {
        using var temporary = new TemporaryDirectory();
        var audioPath = temporary.Combine("track-b.wav");
        File.WriteAllBytes(audioPath, [1, 2, 3]);
        var playlist = new Playlist { Id = "playlist", Name = "Practice" };

        Assert.IsTrue(PlaylistEditor.AddTrack(playlist, "track-a"));
        Assert.IsTrue(PlaylistEditor.AddTrack(playlist, "track-b"));
        Assert.IsTrue(PlaylistEditor.AddTrack(playlist, "track-c"));
        Assert.IsFalse(PlaylistEditor.AddTrack(playlist, "track-b"));
        CollectionAssert.AreEqual(
            new[] { "track-a", "track-b", "track-c" },
            playlist.TrackIds.ToArray());

        Assert.IsFalse(PlaylistEditor.MoveUp(playlist, "track-a"));
        Assert.IsFalse(PlaylistEditor.MoveDown(playlist, "track-c"));
        Assert.IsTrue(PlaylistEditor.MoveUp(playlist, "track-c"));
        CollectionAssert.AreEqual(
            new[] { "track-a", "track-c", "track-b" },
            playlist.TrackIds.ToArray());

        Assert.IsTrue(PlaylistEditor.MoveDown(playlist, "track-a"));
        CollectionAssert.AreEqual(
            new[] { "track-c", "track-a", "track-b" },
            playlist.TrackIds.ToArray());

        Assert.IsTrue(PlaylistEditor.RemoveTrack(playlist, "track-b"));
        Assert.IsFalse(PlaylistEditor.RemoveTrack(playlist, "track-b"));
        CollectionAssert.AreEqual(
            new[] { "track-c", "track-a" },
            playlist.TrackIds.ToArray());
        Assert.IsTrue(File.Exists(audioPath), "Editar una playlist nunca debe borrar el archivo de audio.");
    }
}
