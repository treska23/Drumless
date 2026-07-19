using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class StudioStateStoreTests
{
    [TestMethod]
    public void SaveAndLoad_RoundTripsCompleteState()
    {
        var dateAddedUtc = new DateTimeOffset(2026, 7, 10, 9, 30, 0, TimeSpan.Zero);
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
            Vst3EffectFolders =
            [
                @"D:\Audio\VST3 personalizados"
            ],
            AudioInputMonitors =
            [
                new AudioInputMonitorSetting(
                    0,
                    0.55f,
                    AudioInputProfileKind.Voice,
                    Effects:
                    [
                        AudioEffectSlotSetting.Create(AudioEffectKind.HighPass, 0.42d),
                        AudioEffectSlotSetting.Create(
                            AudioEffectKind.ExternalVst3,
                            mix: 0.75d,
                            externalVst3: new Vst3EffectReference(
                                @"C:\Program Files\Common Files\VST3\Test Effect.vst3",
                                "Test Effect",
                                "ABCDEF0123456789ABCDEF0123456789",
                                "Audio Module Class",
                                "Test Effect",
                                "Test Vendor",
                                "1.0",
                                "VST 3.7",
                                "Fx",
                                @"C:\Presets\Test.vstpreset"))
                    ],
                    EffectsBypassed: true),
                new AudioInputMonitorSetting(1, 0.73f, AudioInputProfileKind.GuitarDrive)
            ],
            AudioEffectBuses =
            [
                new AudioEffectBusSetting(
                    AudioEffectBusTarget.Track,
                    [
                        AudioEffectSlotSetting.Create(AudioEffectKind.Equalizer, 0.61d),
                        AudioEffectSlotSetting.Create(AudioEffectKind.Compressor, 0.44d)
                    ]),
                new AudioEffectBusSetting(
                    AudioEffectBusTarget.Master,
                    [AudioEffectSlotSetting.Create(AudioEffectKind.Saturation, 0.18d, 0.3d)],
                    EffectsBypassed: true)
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
                    DateAddedUtc = dateAddedUtc,
                    Tempo = new TempoSettings(
                        127.5d,
                        0.375d,
                        4,
                        MetronomeEnabled: true,
                        MetronomeVolume: 0.42d,
                        AnalysisConfidence: 0.81d,
                        Segments:
                        [
                            TempoSegment.Create(
                                0d,
                                127.5d,
                                0.375d,
                                confidence: 0.81d,
                                sourceName: "example.test",
                                sourceUrl: "https://example.test/tempo"),
                            TempoSegment.Create(
                                62d,
                                132d,
                                62.1d,
                                confidence: 0.7d,
                                sourceName: "Edición manual")
                        ])
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
            ThumbnailUrl = "https://i.ytimg.com/vi/video12345/hqdefault.jpg",
            Tempo = new TempoSettings(
                96d,
                1.25d,
                MetronomeEnabled: true,
                MetronomeVolume: 0.33d)
        });
        playlist.Items.Add(new PlaylistItem
        {
            Id = "item-original",
            Kind = PlaylistItemKind.LocalTrack,
            TrackId = "track-original",
            Title = "Original"
        });
        state.Playlists.Add(playlist);
        state.AnalysisRecords.Add(new MediaAnalysisRecord
        {
            MediaKey = "local:track-original",
            Tempo = state.Tracks[0].Tempo,
            TempoOrigin = TempoAnalysisOrigin.ManuallyAdjusted,
            TempoUpdatedAtUtc = new DateTimeOffset(2026, 7, 16, 8, 0, 0, TimeSpan.Zero),
            SongStructure = new SongStructureMap(
                new DateTimeOffset(2026, 7, 16, 8, 2, 0, TimeSpan.Zero),
                180d,
                0.74d,
                [
                    new SongSection("section-a", 0d, 42d, "Sección A", 0.8d, "0.2|0.4"),
                    new SongSection("section-b", 42d, 80d, "Sección B", 0.68d, "0.7|0.8")
                ]),
            ChordSheet = new ChordSheetDocument(
                "sheet-1",
                "Original",
                ChordSheetSourceKind.WebSelection,
                "https://example.test/chords",
                "[Em]Primera línea",
                new DateTimeOffset(2026, 7, 16, 8, 3, 0, TimeSpan.Zero),
                1.5d,
                [
                    new ChordSheetLine(
                        "line-1",
                        0,
                        ChordSheetLineKind.Chords,
                        "Em",
                        0d,
                        0.5d,
                        "Estrofa"),
                    new ChordSheetLine(
                        "line-2",
                        1,
                        ChordSheetLineKind.Lyrics,
                        "Primera línea",
                        1.2d,
                        1d,
                        "Estrofa")
                ]),
            DrumReference = new DrumReferenceMap(
                "reference-v1",
                originalPath,
                new DateTimeOffset(2026, 7, 16, 8, 1, 0, TimeSpan.Zero),
                0.87d,
                [0.5d, 1d, 1.5d]),
            PerformanceSessions =
            [
                new DrumPerformanceSession(
                    "session-1",
                    new DateTimeOffset(2026, 7, 16, 8, 5, 0, TimeSpan.Zero),
                    true,
                    23.5d,
                    64,
                    59,
                    2,
                    3,
                    92.1875d,
                    17.4d,
                    61d,
                    ExpectedHits: 66,
                    MissedHits: 2,
                    ExtraHits: 1,
                    ReferenceVersion: "reference-v1")
            ]
        });

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
        Assert.AreEqual(AudioInputProfileKind.Voice, loaded.AudioInputMonitors[0].Profile);
        Assert.IsTrue(loaded.AudioInputMonitors[0].EffectsBypassed);
        Assert.AreEqual(1, loaded.AudioInputMonitors[0].EffectiveEffects.Count);
        Assert.AreEqual(
            AudioEffectKind.ExternalVst3,
            loaded.AudioInputMonitors[0].EffectiveEffects[0].Kind);
        Assert.AreEqual(
            "Test Effect",
            loaded.AudioInputMonitors[0].EffectiveEffects[0].ExternalVst3?.Name);
        Assert.AreEqual(
            @"C:\Presets\Test.vstpreset",
            loaded.AudioInputMonitors[0].EffectiveEffects[0].ExternalVst3?.PresetPath);
        Assert.AreEqual(1, loaded.AudioInputMonitors[1].ChannelIndex);
        Assert.AreEqual(0.73f, loaded.AudioInputMonitors[1].Gain);
        Assert.AreEqual(AudioInputProfileKind.GuitarDrive, loaded.AudioInputMonitors[1].Profile);
        Assert.AreEqual(2, loaded.AudioEffectBuses.Count);
        Assert.AreEqual(AudioEffectBusTarget.Track, loaded.AudioEffectBuses[0].Target);
        Assert.AreEqual(0, loaded.AudioEffectBuses[0].EffectiveEffects.Count);
        Assert.AreEqual(AudioEffectBusTarget.Master, loaded.AudioEffectBuses[1].Target);
        Assert.IsTrue(loaded.AudioEffectBuses[1].EffectsBypassed);
        CollectionAssert.AreEqual(
            new[] { @"D:\Audio\VST3 personalizados" },
            loaded.Vst3EffectFolders);
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
        Assert.AreEqual(dateAddedUtc, loaded.Tracks[0].DateAddedUtc);
        var loadedTempo = loaded.Tracks[0].Tempo;
        Assert.IsNotNull(loadedTempo);
        Assert.AreEqual(127.5d, loadedTempo.Bpm);
        Assert.AreEqual(0.375d, loadedTempo.FirstBeatSeconds);
        Assert.IsTrue(loadedTempo.MetronomeEnabled);
        Assert.AreEqual(0.42d, loadedTempo.MetronomeVolume);
        Assert.AreEqual(2, loadedTempo.EffectiveSegments.Count);
        Assert.AreEqual(132d, loadedTempo.EffectiveSegments[1].Bpm);
        Assert.AreEqual("https://example.test/tempo", loadedTempo.EffectiveSegments[0].SourceUrl);
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
        var loadedYouTubeTempo = loaded.Playlists[0].Items[1].Tempo;
        Assert.IsNotNull(loadedYouTubeTempo);
        Assert.AreEqual(96d, loadedYouTubeTempo.Bpm);
        Assert.AreEqual(1.25d, loadedYouTubeTempo.FirstBeatSeconds);
        Assert.IsTrue(loadedYouTubeTempo.MetronomeEnabled);
        Assert.AreEqual("track-original", loaded.Playlists[0].Items[2].TrackId);
        Assert.AreEqual(2, loaded.AnalysisRecords.Count);
        var loadedAnalysis = loaded.AnalysisRecords.Single(record =>
            record.MediaKey == "local:track-original");
        Assert.AreEqual(TempoAnalysisOrigin.ManuallyAdjusted, loadedAnalysis.TempoOrigin);
        Assert.IsNotNull(loadedAnalysis.SongStructure);
        Assert.AreEqual(2, loadedAnalysis.SongStructure.Sections.Count);
        Assert.AreEqual("Sección B", loadedAnalysis.SongStructure.Sections[1].Label);
        Assert.IsNotNull(loadedAnalysis.ChordSheet);
        Assert.AreEqual("https://example.test/chords", loadedAnalysis.ChordSheet.SourceUrl);
        Assert.AreEqual(2, loadedAnalysis.ChordSheet.Lines.Count);
        Assert.AreEqual(1.2d, loadedAnalysis.ChordSheet.Lines[1].StartSeconds);
        Assert.AreEqual(1.5d, loadedAnalysis.ChordSheet.LeadSeconds);
        Assert.AreEqual(1, loadedAnalysis.PerformanceSessions.Count);
        Assert.IsNotNull(loadedAnalysis.DrumReference);
        Assert.AreEqual("reference-v1", loadedAnalysis.DrumReference.Version);
        Assert.AreEqual(3, loadedAnalysis.DrumReference.HitTimesSeconds.Count);
        Assert.AreEqual(92.1875d, loadedAnalysis.PerformanceSessions[0].AccuracyPercent);
        Assert.IsTrue(loadedAnalysis.PerformanceSessions[0].FinishedAtNaturalEnd);
        Assert.AreEqual(2, loadedAnalysis.PerformanceSessions[0].MissedHits);
        Assert.AreEqual("reference-v1", loadedAnalysis.PerformanceSessions[0].ReferenceVersion);
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

    [TestMethod]
    public void Load_VersionThreeInputMonitorWithoutProfile_DefaultsToClean()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.Combine("studio-state.json");
        File.WriteAllText(
            statePath,
            """
            {
              "schemaVersion": 3,
              "outputFolder": "C:\\Audio",
              "audioInputMonitors": [
                {
                  "channelIndex": 2,
                  "gain": 0.65
                }
              ],
              "playbackMode": "sequential"
            }
            """);
        var store = new StudioStateStore(statePath);

        var loaded = store.Load();

        Assert.IsNull(store.LastLoadWarning);
        Assert.AreEqual(1, loaded.AudioInputMonitors.Count);
        Assert.AreEqual(2, loaded.AudioInputMonitors[0].ChannelIndex);
        Assert.AreEqual(0.65f, loaded.AudioInputMonitors[0].Gain);
        Assert.AreEqual(AudioInputProfileKind.Clean, loaded.AudioInputMonitors[0].Profile);
    }

    [TestMethod]
    public void Load_VersionOneDrumlessSelection_AddsNewGuitarAndPianoStems()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.Combine("studio-state.json");
        File.WriteAllText(
            statePath,
            """
            {
              "schemaVersion": 1,
              "outputFolder": "C:\\Audio",
              "stemSelection": "bass, vocals, other",
              "playbackMode": "sequential"
            }
            """);

        var loaded = new StudioStateStore(statePath).Load();

        Assert.AreEqual(StemSelection.Drumless, loaded.StemSelection);
        Assert.IsTrue(loaded.StemSelection.HasFlag(StemSelection.Guitar));
        Assert.IsTrue(loaded.StemSelection.HasFlag(StemSelection.Piano));
    }

    [TestMethod]
    public void Load_VersionTwoEmbeddedTempo_MigratesToNormalizedAnalysisDatabase()
    {
        using var temporary = new TemporaryDirectory();
        var statePath = temporary.Combine("studio-state.json");
        File.WriteAllText(
            statePath,
            """
            {
              "schemaVersion": 2,
              "outputFolder": "C:\\Audio",
              "tracks": [
                {
                  "id": "track-a",
                  "title": "Local",
                  "path": "C:\\Audio\\local.wav",
                  "variant": "original",
                  "tempo": { "bpm": 123, "firstBeatSeconds": 0.4, "analysisConfidence": 0.75 }
                }
              ],
              "playlists": [
                {
                  "id": "playlist-a",
                  "name": "Mixed",
                  "items": [
                    {
                      "id": "video-a",
                      "kind": "youTube",
                      "youTubeVideoId": "abc123",
                      "youTubeUrl": "https://www.youtube.com/watch?v=abc123",
                      "title": "Video",
                      "tempo": { "bpm": 98, "firstBeatSeconds": 1.2 }
                    }
                  ]
                }
              ]
            }
            """);

        var loaded = new StudioStateStore(statePath).Load();

        Assert.AreEqual(2, loaded.AnalysisRecords.Count);
        Assert.AreEqual(
            TempoAnalysisOrigin.Automatic,
            loaded.AnalysisRecords.Single(record => record.MediaKey == "local:track-a").TempoOrigin);
        Assert.AreEqual(
            98d,
            loaded.AnalysisRecords.Single(record => record.MediaKey == "youtube:abc123").Tempo?.Bpm);
        Assert.AreEqual(98d, loaded.Playlists[0].Items[0].Tempo?.Bpm);
    }
}
