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
            AudioInputMonitors =
            [
                new AudioInputMonitorSetting(0, 0.55f),
                new AudioInputMonitorSetting(1, 0.73f)
            ],
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
            StemSelection = StemSelection.Drums | StemSelection.Bass,
            PerformanceLatencyCompensationMs = 23.5d,
            Tracks =
            [
                new TrackRecord
                {
                    Id = "track-original",
                    Title = "Original",
                    Path = originalPath,
                    Variant = TrackVariant.Original,
                    Tempo = new TempoSettings(
                        127.5d,
                        0.375d,
                        4,
                        MetronomeEnabled: true,
                        MetronomeVolume: 0.42d,
                        AnalysisConfidence: 0.81d)
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
        var playlist = new Playlist
        {
            Id = "playlist-practice",
            Name = "Práctica diaria",
            IsIncludedInMix = true
        };
        playlist.Items.Add(new PlaylistItem
        {
            Id = "item-local",
            Kind = PlaylistItemKind.LocalTrack,
            TrackId = "track-drumless",
            Title = "Sin batería"
        });
        playlist.Items.Add(new PlaylistItem
        {
            Id = "item-youtube",
            Kind = PlaylistItemKind.YouTube,
            YouTubeVideoId = "video12345",
            YouTubeUrl = "https://www.youtube.com/watch?v=video12345",
            Title = "Backing track",
            ThumbnailUrl = "https://i.ytimg.com/vi/video12345/hqdefault.jpg"
        });
        playlist.Items.Add(new PlaylistItem
        {
            Id = "item-original",
            Kind = PlaylistItemKind.LocalTrack,
            TrackId = "track-original",
            Title = "Original"
        });
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
        Assert.AreEqual(2, loaded.AudioInputMonitors.Count);
        Assert.AreEqual(0, loaded.AudioInputMonitors[0].ChannelIndex);
        Assert.AreEqual(0.55f, loaded.AudioInputMonitors[0].Gain);
        Assert.AreEqual(1, loaded.AudioInputMonitors[1].ChannelIndex);
        Assert.AreEqual(0.73f, loaded.AudioInputMonitors[1].Gain);
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
        Assert.AreEqual(StemSelection.Drums | StemSelection.Bass, loaded.StemSelection);
        Assert.AreEqual(23.5d, loaded.PerformanceLatencyCompensationMs);
        Assert.AreEqual(2, loaded.Tracks.Count);
        Assert.AreEqual("track-original", loaded.Tracks[0].Id);
        Assert.AreEqual(originalPath, loaded.Tracks[0].Path);
        Assert.AreEqual(TrackVariant.Original, loaded.Tracks[0].Variant);
        var loadedTempo = loaded.Tracks[0].Tempo;
        Assert.IsNotNull(loadedTempo);
        Assert.AreEqual(127.5d, loadedTempo.Bpm);
        Assert.AreEqual(0.375d, loadedTempo.FirstBeatSeconds);
        Assert.IsTrue(loadedTempo.MetronomeEnabled);
        Assert.AreEqual(0.42d, loadedTempo.MetronomeVolume);
        Assert.AreEqual("track-drumless", loaded.Tracks[1].Id);
        Assert.AreEqual(TrackVariant.GeneratedDrumless, loaded.Tracks[1].Variant);
        Assert.AreEqual(1, loaded.Playlists.Count);
        Assert.AreEqual("Práctica diaria", loaded.Playlists[0].Name);
        Assert.IsTrue(loaded.Playlists[0].IsIncludedInMix);
        Assert.AreEqual(3, loaded.Playlists[0].Items.Count);
        Assert.AreEqual(PlaylistItemKind.LocalTrack, loaded.Playlists[0].Items[0].Kind);
        Assert.AreEqual("track-drumless", loaded.Playlists[0].Items[0].TrackId);
        Assert.AreEqual(PlaylistItemKind.YouTube, loaded.Playlists[0].Items[1].Kind);
        Assert.AreEqual("video12345", loaded.Playlists[0].Items[1].YouTubeVideoId);
        Assert.AreEqual("Backing track", loaded.Playlists[0].Items[1].Title);
        Assert.AreEqual("track-original", loaded.Playlists[0].Items[2].TrackId);
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
        Assert.AreEqual(StemSelection.Drumless, loaded.StemSelection);
        Assert.IsFalse(loaded.Playlists.Any(playlist => playlist.IsIncludedInMix));
    }

    [TestMethod]
    public void Load_OlderPlaylistWithoutMixFlag_DefaultsToNotIncluded()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.Combine("studio-state.json");
        File.WriteAllText(
            statePath,
            """
            {
              "schemaVersion": 1,
              "outputFolder": "C:\\Audio",
              "playlists": [
                {
                  "id": "legacy-playlist",
                  "name": "Legacy",
                  "trackIds": ["track-a"]
                }
              ],
              "playbackMode": "sequential"
            }
            """);
        var store = new StudioStateStore(statePath);

        var loaded = store.Load();

        Assert.IsNull(store.LastLoadWarning);
        Assert.AreEqual(1, loaded.Playlists.Count);
        Assert.IsFalse(loaded.Playlists[0].IsIncludedInMix);
        Assert.AreEqual(1, loaded.Playlists[0].Items.Count);
        Assert.AreEqual(PlaylistItemKind.LocalTrack, loaded.Playlists[0].Items[0].Kind);
        Assert.AreEqual("track-a", loaded.Playlists[0].Items[0].TrackId);
    }
}
