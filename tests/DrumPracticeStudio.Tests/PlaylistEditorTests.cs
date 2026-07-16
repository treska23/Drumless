using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class PlaylistEditorTests
{
    [TestMethod]
    public void MixedEditOperations_PreserveOrderPreventDuplicatesAndNeverDeleteAudio()
    {
        using var temporary = new TemporaryDirectory();
        var audioPath = temporary.Combine("track-b.wav");
        File.WriteAllBytes(audioPath, [1, 2, 3]);
        var playlist = new Playlist { Id = "playlist", Name = "Practice" };
        var a = Local("track-a", temporary.Combine("a.wav"));
        var b = Local("track-b", audioPath);

        Assert.IsTrue(PlaylistEditor.AddTrack(playlist, a));
        Assert.IsTrue(PlaylistEditor.AddYouTube(
            playlist, "video12345", "https://www.youtube.com/watch?v=video12345", "Video"));
        Assert.IsTrue(PlaylistEditor.AddTrack(playlist, b));
        Assert.IsFalse(PlaylistEditor.AddTrack(playlist, b));
        Assert.IsFalse(PlaylistEditor.AddYouTube(
            playlist, "video12345", "https://youtu.be/video12345", "Duplicado"));
        CollectionAssert.AreEqual(
            new[] { "local:track-a", "youtube:video12345", "local:track-b" },
            playlist.Items.Select(item => item.MediaKey).ToArray());

        var bItem = playlist.Items.Single(item => item.TrackId == "track-b");
        Assert.IsTrue(PlaylistEditor.MoveUp(playlist, bItem.Id));
        CollectionAssert.AreEqual(
            new[] { "local:track-a", "local:track-b", "youtube:video12345" },
            playlist.Items.Select(item => item.MediaKey).ToArray());
        Assert.IsTrue(PlaylistEditor.RemoveItem(playlist, bItem.Id));
        Assert.IsFalse(PlaylistEditor.RemoveItem(playlist, bItem.Id));
        Assert.IsTrue(File.Exists(audioPath), "Editar una playlist nunca debe borrar el archivo de audio.");
    }

    [TestMethod]
    public void MoveTo_ReordersMixedItemsToAnyPositionAndBoundsTheTarget()
    {
        var playlist = new Playlist { Id = "playlist", Name = "Practice" };
        foreach (var id in new[] { "a", "b", "c", "d" })
        {
            playlist.Items.Add(new PlaylistItem
            {
                Id = id,
                Kind = PlaylistItemKind.LocalTrack,
                TrackId = $"track-{id}",
                Title = id
            });
        }

        Assert.IsTrue(PlaylistEditor.MoveTo(playlist, "d", 1));
        CollectionAssert.AreEqual(new[] { "a", "d", "b", "c" }, playlist.Items.Select(x => x.Id).ToArray());
        Assert.IsTrue(PlaylistEditor.MoveTo(playlist, "a", 99));
        CollectionAssert.AreEqual(new[] { "d", "b", "c", "a" }, playlist.Items.Select(x => x.Id).ToArray());
    }

    [TestMethod]
    public void AddYouTubeRange_ImportsWholePlaylistInOrderAndSkipsDuplicates()
    {
        var playlist = new Playlist { Id = "playlist", Name = "Practice" };
        PlaylistEditor.AddYouTube(
            playlist,
            "video00001",
            "https://www.youtube.com/watch?v=video00001",
            "Existing");
        var entries = new[]
        {
            new YouTubePlaylistEntry(
                "video00001", "Existing", "https://www.youtube.com/watch?v=video00001", null),
            new YouTubePlaylistEntry(
                "video00002", "Second", "https://www.youtube.com/watch?v=video00002", "thumb-2"),
            new YouTubePlaylistEntry(
                "video00003", "Third", "https://www.youtube.com/watch?v=video00003", "thumb-3")
        };

        var added = PlaylistEditor.AddYouTubeRange(playlist, entries);

        Assert.AreEqual(2, added);
        CollectionAssert.AreEqual(
            new[] { "video00001", "video00002", "video00003" },
            playlist.Items.Select(item => item.YouTubeVideoId).ToArray());
        Assert.AreEqual("Second", playlist.Items[1].Title);
        Assert.AreEqual("thumb-3", playlist.Items[2].ThumbnailUrl);
    }

    private static LocalTrack Local(string id, string path) => new()
    {
        Id = id,
        Title = id,
        Path = path,
        Variant = TrackVariant.Original,
        IsMissing = !File.Exists(path)
    };
}
