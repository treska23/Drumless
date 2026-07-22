using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class SongEffectProfilePersistenceTests
{
    [TestMethod]
    public void StudioStateStore_RoundTripsSongEffectProfile()
    {
        using var temporary = new TemporaryDirectory();
        var store = new StudioStateStore(temporary.Combine("state.json"));
        var effect = new Vst3EffectReference(
            @"C:\VST3\Effect.vst3",
            "Effect",
            "0123456789ABCDEF0123456789ABCDEF",
            "Audio Module Class",
            "Effect",
            "Vendor",
            "1.0",
            "VST 3.7",
            "Fx|Dynamics",
            @"C:\Presets\Vocal.vstpreset",
            [new Vst3ParameterSetting(17, "Threshold", 0.42d)]);
        var profile = new SongEffectProfile(
            "profile-1",
            "local:track-1",
            "Original aproximado",
            "Artist - Song.wav",
            "Artist",
            "Song",
            new DateTimeOffset(2026, 7, 22, 18, 0, 0, TimeSpan.Zero),
            "llama-test",
            "Cadena conservadora",
            new SongInputEffectChain(
                0,
                "Guitarra mono",
                "Drive",
                [new SongEffectSlotRecommendation(effect, "Amp", "Drive", "Crunch", 0.8d)]),
            new SongInputEffectChain(
                1,
                "Voz mono",
                "Control",
                [new SongEffectSlotRecommendation(effect, "Dynamics", "Compresión", "Gentle", 0.6d)]));
        var state = new StudioState();
        state.AnalysisRecords.Add(new MediaAnalysisRecord
        {
            MediaKey = "local:track-1",
            SongEffectProfile = profile
        });

        store.Save(state);
        var loaded = store.Load();
        var restored = loaded.AnalysisRecords.Single().SongEffectProfile;

        Assert.IsNotNull(restored);
        Assert.AreEqual("Artist", restored.Artist);
        Assert.AreEqual("Song", restored.SongTitle);
        Assert.AreEqual(0, restored.Guitar.ChannelIndex);
        Assert.AreEqual(1, restored.Voice.ChannelIndex);
        Assert.AreEqual(0.8d, restored.Guitar.Slots[0].Mix);
        Assert.AreEqual(@"C:\Presets\Vocal.vstpreset", restored.Guitar.Slots[0].Effect.PresetPath);
        Assert.AreEqual(17u, restored.Guitar.Slots[0].Effect.EffectiveParameterSettings[0].Id);
        Assert.AreEqual(0.42d, restored.Guitar.Slots[0].Effect.EffectiveParameterSettings[0].NormalizedValue);
    }
}
