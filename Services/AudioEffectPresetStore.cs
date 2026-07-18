using System.Text.Json;
using System.Text.Json.Serialization;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed record AudioEffectChainPreset(
    string Name,
    IReadOnlyList<AudioEffectSlotSetting> Effects,
    bool EffectsBypassed = false);

public sealed class AudioEffectPresetStore
{
    private const int SchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public void Save(string path, AudioEffectChainPreset preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(preset);
        var normalized = Normalize(preset);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(
                temporary,
                JsonSerializer.Serialize(
                    new PresetDocument(
                        SchemaVersion,
                        normalized.Name,
                        normalized.Effects,
                        normalized.EffectsBypassed),
                    JsonOptions));
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
            }
        }
    }

    public AudioEffectChainPreset Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var document = JsonSerializer.Deserialize<PresetDocument>(
                           File.ReadAllText(Path.GetFullPath(path)),
                           JsonOptions)
                       ?? throw new InvalidDataException("El preset de efectos está vacío.");
        if (document.SchemaVersion != SchemaVersion)
        {
            throw new NotSupportedException(
                $"La versión {document.SchemaVersion} del preset no es compatible.");
        }
        return Normalize(new AudioEffectChainPreset(
            document.Name,
            document.Effects ?? [],
            document.EffectsBypassed));
    }

    private static AudioEffectChainPreset Normalize(AudioEffectChainPreset preset)
    {
        var name = string.IsNullOrWhiteSpace(preset.Name)
            ? "Cadena personal"
            : preset.Name.Trim();
        var effects = preset.Effects
            .Where(effect => effect is not null)
            .Take(AudioEffectCatalog.MaximumSlots)
            .Select(AudioEffectSlotSetting.Normalize)
            .ToArray();
        return preset with { Name = name, Effects = effects };
    }

    private sealed record PresetDocument(
        int SchemaVersion,
        string Name,
        IReadOnlyList<AudioEffectSlotSetting>? Effects,
        bool EffectsBypassed);
}
