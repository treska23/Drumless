using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class PlaylistMixServiceTests
{
    [TestMethod]
    public void BuildQueue_CombinesIncludedPlaylistsInOrderWithoutDuplicates()
    {
        var warmup = CreatePlaylist("warmup", true, "track-a", "track-b");
        var ignored = CreatePlaylist("ignored", false, "track-x");
        var songs = CreatePlaylist("songs", true, "track-b", "track-c", "track-d");

        var queue = PlaylistMixService.BuildQueue([warmup, ignored, songs], ignored);

        CollectionAssert.AreEqual(
            new[] { "track-a", "track-b", "track-c", "track-d" },
            queue.ToArray());
    }

    [TestMethod]
    public void BuildQueue_UsesSelectedPlaylistWhenNoMixIsEnabled()
    {
        var selected = CreatePlaylist("selected", false, "track-b", "track-a");
        var other = CreatePlaylist("other", false, "track-x");

        var queue = PlaylistMixService.BuildQueue([other, selected], selected);

        CollectionAssert.AreEqual(new[] { "track-b", "track-a" }, queue.ToArray());
    }

    [TestMethod]
    public void BuildQueue_WithNoMixAndNoSelection_IsEmpty()
    {
        var playlist = CreatePlaylist("playlist", false, "track-a");

        var queue = PlaylistMixService.BuildQueue([playlist], fallbackPlaylist: null);

        Assert.AreEqual(0, queue.Count);
    }

    private static Playlist CreatePlaylist(string id, bool included, params string[] trackIds)
    {
        var playlist = new Playlist
        {
            Id = id,
            Name = id,
            IsIncludedInMix = included
        };
        foreach (var trackId in trackIds)
        {
            playlist.TrackIds.Add(trackId);
        }

        return playlist;
    }
}
