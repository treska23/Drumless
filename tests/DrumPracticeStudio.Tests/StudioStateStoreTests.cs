using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class StudioStateStoreTests
{
    [TestMethod]
    public void SaveAndLoad_RoundTripsCompleteState()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.Combine("state", "studio-state.json");
        var outputFolder = temporary.Combine("generated");
        var originalPath = temporary.Combine("music", "original.wav");
        var drumlessPath = temporary.Combine("generated", "drumless.wav");
        var store = new StudioStateStore(statePath);
        var state = new StudioState
        {
            OutputFolder = outputFolder,
            SelectedPlaylistId = "playlist-practice",
            PlaybackMode = PlaybackMode.Shuffle,
            Tracks =
            [
                new TrackRecord
                {
                    Id = "track-original",
                    Title = "Original",
                    Path = originalPath,
                    Variant = TrackVariant.Original
                },
                new TrackRecord
                {
                    Id = "track-drumless",
                    Title = "Sin batería",
                    Path = drumlessPath,
                    Variant = TrackVariant.GeneratedDrumless
                }
            ]
        };
        var playlist = new Playlist { Id = "playlist-practice", Name = "Práctica diaria" };
        playlist.TrackIds.Add("track-drumless");
        playlist.TrackIds.Add("track-original");
        state.Playlists.Add(playlist);

        store.Save(state);
        var loaded = store.Load();

        Assert.IsNull(store.LastLoadWarning);
        Assert.AreEqual(outputFolder, loaded.OutputFolder);
        Assert.AreEqual("playlist-practice", loaded.SelectedPlaylistId);
        Assert.AreEqual(PlaybackMode.Shuffle, loaded.PlaybackMode);
        Assert.AreEqual(2, loaded.Tracks.Count);
        Assert.AreEqual("track-original", loaded.Tracks[0].Id);
        Assert.AreEqual(originalPath, loaded.Tracks[0].Path);
        Assert.AreEqual(TrackVariant.Original, loaded.Tracks[0].Variant);
        Assert.AreEqual("track-drumless", loaded.Tracks[1].Id);
        Assert.AreEqual(TrackVariant.GeneratedDrumless, loaded.Tracks[1].Variant);
        Assert.AreEqual(1, loaded.Playlists.Count);
        Assert.AreEqual("Práctica diaria", loaded.Playlists[0].Name);
        CollectionAssert.AreEqual(
            new[] { "track-drumless", "track-original" },
            loaded.Playlists[0].TrackIds.ToArray());
    }

    [TestMethod]
    public void Load_WithCorruptJson_ReturnsSafeDefaultsAndWarning()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.Combine("studio-state.json");
        File.WriteAllText(statePath, "{ esto no es JSON");
        var store = new StudioStateStore(statePath);

        var loaded = store.Load();

        Assert.IsFalse(string.IsNullOrWhiteSpace(store.LastLoadWarning));
        Assert.AreEqual(AppPaths.DerivedTracks, loaded.OutputFolder);
        Assert.AreEqual(PlaybackMode.Sequential, loaded.PlaybackMode);
        Assert.AreEqual(0, loaded.Tracks.Count);
        Assert.AreEqual(0, loaded.Playlists.Count);
        Assert.IsNull(loaded.SelectedPlaylistId);
    }
}
