using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class PlaylistMixServiceTests
{
    [TestMethod]
    public void BuildQueue_CombinesLocalAndYouTubeInOrderWithoutMediaDuplicates()
    {
        var warmup = CreatePlaylist("warmup", true,
            Local("a"), YouTube("video11111"), Local("b"));
        var ignored = CreatePlaylist("ignored", false, Local("x"));
        var songs = CreatePlaylist("songs", true,
            YouTube("video11111"), Local("b"), YouTube("video22222"));

        var queue = PlaylistMixService.BuildQueue([warmup, ignored, songs], ignored);

        CollectionAssert.AreEqual(
            new[] { "local:a", "youtube:video11111", "local:b", "youtube:video22222" },
            queue.Select(item => item.MediaKey).ToArray());
    }

    [TestMethod]
    public void BuildQueue_UsesSelectedPlaylistWhenNoMixIsEnabled()
    {
        var selected = CreatePlaylist("selected", false, YouTube("video11111"), Local("a"));
        var other = CreatePlaylist("other", false, Local("x"));

        var queue = PlaylistMixService.BuildQueue([other, selected], selected);

        CollectionAssert.AreEqual(
            new[] { "youtube:video11111", "local:a" },
            queue.Select(item => item.MediaKey).ToArray());
    }

    private static Playlist CreatePlaylist(string id, bool included, params PlaylistItem[] items)
    {
        var playlist = new Playlist { Id = id, Name = id, IsIncludedInMix = included };
        foreach (var item in items) playlist.Items.Add(item);
        return playlist;
    }

    private static PlaylistItem Local(string id) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Kind = PlaylistItemKind.LocalTrack,
        TrackId = id, Title = id
    };

    private static PlaylistItem YouTube(string id) => new()
    {
        Id = Guid.NewGuid().ToString("N"), Kind = PlaylistItemKind.YouTube,
        YouTubeVideoId = id, YouTubeUrl = $"https://www.youtube.com/watch?v={id}", Title = id
    };
}
