using System.Text.Json.Serialization;

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

    [JsonIgnore]
    public string AppliedConfiguration
    {
        get
        {
            var parameterCount = Effect.EffectiveParameterSettings.Count;
            if (parameterCount > 0 && !string.IsNullOrWhiteSpace(Effect.PresetPath))
            {
                return $"Preset base · {FormatParameters()}";
            }
            if (parameterCount > 0)
            {
                return FormatParameters();
            }
            return string.IsNullOrWhiteSpace(Effect.PresetPath)
                ? "Sin configuración aplicada"
                : $"Preset base: {Path.GetFileNameWithoutExtension(Effect.PresetPath)}";
        }
    }

    private string FormatParameters() => "Adaptados: " + string.Join(
        " · ",
        Effect.EffectiveParameterSettings
            .Take(6)
            .Select(setting => $"{setting.Title} {setting.NormalizedValue:P0}"));
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
