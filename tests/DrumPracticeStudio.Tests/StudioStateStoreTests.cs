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
            AudioOutputDeviceId = "asio:Focusrite USB ASIO",
            AudioInputOutputDeviceId = "asio:Focusrite USB ASIO",
            AudioInputChannelIndex = 1,
            AudioInputGain = 0.73d,
            MidiDeviceName = "MPK mini 3",
            MidiDeviceIndex = 2,
            AutoConnectMidi = true,
            MidiVelocitySensitivity = 84d,
            ActiveLibraryId = "factory.natural",
            ActiveKitId = "factory.natural.studio",
            TrackVolume = 0.62d,
            VstModulePath = @"C:\Program Files\Common Files\VST3\Groove Agent SE.vst3",
            VstClassId = "0123456789ABCDEF0123456789ABCDEF",
            AutoLoadVst = true,
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
        Assert.AreEqual("asio:Focusrite USB ASIO", loaded.AudioOutputDeviceId);
        Assert.AreEqual("asio:Focusrite USB ASIO", loaded.AudioInputOutputDeviceId);
        Assert.AreEqual(1, loaded.AudioInputChannelIndex);
        Assert.AreEqual(0.73d, loaded.AudioInputGain);
        Assert.AreEqual("MPK mini 3", loaded.MidiDeviceName);
        Assert.AreEqual(2, loaded.MidiDeviceIndex.GetValueOrDefault());
        Assert.IsTrue(loaded.AutoConnectMidi);
        Assert.AreEqual(84d, loaded.MidiVelocitySensitivity);
        Assert.AreEqual("factory.natural", loaded.ActiveLibraryId);
        Assert.AreEqual("factory.natural.studio", loaded.ActiveKitId);
        Assert.AreEqual(0.62d, loaded.TrackVolume);
        Assert.AreEqual(
            @"C:\Program Files\Common Files\VST3\Groove Agent SE.vst3",
            loaded.VstModulePath);
        Assert.AreEqual("0123456789ABCDEF0123456789ABCDEF", loaded.VstClassId);
        Assert.IsTrue(loaded.AutoLoadVst);
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
        Assert.IsTrue(loaded.AutoConnectMidi);
        Assert.AreEqual(72d, loaded.MidiVelocitySensitivity);
        Assert.AreEqual(0.8d, loaded.TrackVolume);
        Assert.AreEqual(0.8d, loaded.AudioInputGain);
    }

    [TestMethod]
    public void Load_OlderVersionOneDocument_UsesNewSettingDefaults()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.Combine("studio-state.json");
        File.WriteAllText(
            statePath,
            """
            {
              "schemaVersion": 1,
              "outputFolder": "C:\\Audio",
              "playbackMode": "sequential"
            }
            """);
        var store = new StudioStateStore(statePath);

        var loaded = store.Load();

        Assert.IsNull(store.LastLoadWarning);
        Assert.IsTrue(loaded.AutoConnectMidi);
        Assert.AreEqual(72d, loaded.MidiVelocitySensitivity);
        Assert.AreEqual(0.8d, loaded.TrackVolume);
        Assert.AreEqual(0.8d, loaded.AudioInputGain);
        Assert.IsNull(loaded.AudioInputChannelIndex);
        Assert.IsFalse(loaded.AutoLoadVst);
    }
}
