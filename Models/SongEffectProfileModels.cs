namespace DrumPracticeStudio.Models;

public sealed record InstalledEffectDescriptor(
    string CatalogId,
    string EffectType,
    Vst3EffectReference Reference);

public sealed record SongEffectRecommendationRequest(
    string MediaKey,
    string TrackTitle,
    string Artist,
    string SongTitle,
    double? Bpm,
    IReadOnlyList<string> SongSections,
    IReadOnlyList<InstalledEffectDescriptor> AvailableEffects);

public sealed record SongEffectSlotRecommendation(
    Vst3EffectReference Effect,
    string EffectType,
    string Purpose,
    string PresetHint,
    double Mix = 1d)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Effect.Vendor)
        ? Effect.Name
        : $"{Effect.Name} · {Effect.Vendor}";

    public string Detail => string.IsNullOrWhiteSpace(PresetHint)
        ? Purpose
        : $"{Purpose} · preset sugerido: {PresetHint}";
}

public sealed record SongInputEffectChain(
    int ChannelIndex,
    string Instrument,
    string Description,
    IReadOnlyList<SongEffectSlotRecommendation> Slots)
{
    public string InputLabel => $"Input {ChannelIndex + 1} · {Instrument}";
}

public sealed record SongEffectProfile(
    string Id,
    string MediaKey,
    string Name,
    string TrackTitle,
    string Artist,
    string SongTitle,
    DateTimeOffset CreatedAtUtc,
    string OllamaModel,
    string Summary,
    SongInputEffectChain Guitar,
    SongInputEffectChain Voice);
