using System.Text.Json;
using System.Text.Json.Serialization;
using DrumPracticeStudio.Models;
using NAudio.Vst3;

namespace DrumPracticeStudio.Services;

public sealed record AudioEffectChainPreset(
    string Name,
    IReadOnlyList<AudioEffectSlotSetting> Effects,
    bool EffectsBypassed = false,
    IReadOnlyDictionary<string, byte[]>? PluginStates = null);

public sealed class AudioEffectPresetStore
{
    private const int LegacySchemaVersion = 1;
    private const int SchemaVersion = 2;
    private const long MaximumPluginStateBytes = 64L * 1024 * 1024;
    private readonly string _stateDirectory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public AudioEffectPresetStore(string? stateDirectory = null)
    {
        _stateDirectory = Path.GetFullPath(stateDirectory ?? AppPaths.VstStates);
    }

    public void Save(string path, AudioEffectChainPreset preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(preset);
        var normalized = Normalize(preset);
        ValidatePluginStates(normalized.Effects, normalized.PluginStates);
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
                        normalized.EffectsBypassed,
                        normalized.PluginStates),
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
        if (document.SchemaVersion is not (LegacySchemaVersion or SchemaVersion))
        {
            throw new NotSupportedException(
                $"La versión {document.SchemaVersion} del preset no es compatible.");
        }

        var normalized = Normalize(new AudioEffectChainPreset(
            document.Name,
            document.Effects ?? [],
            document.EffectsBypassed,
            document.SchemaVersion == SchemaVersion ? document.PluginStates : null));
        if (document.SchemaVersion == LegacySchemaVersion)
        {
            normalized = RecoverLegacyAutomaticStates(normalized);
        }
        return ImportWithNewSlotIds(normalized);
    }

    private AudioEffectChainPreset RecoverLegacyAutomaticStates(AudioEffectChainPreset preset)
    {
        var pluginStates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var effect in preset.Effects)
        {
            var reference = effect.ExternalVst3!;
            var statePath = Vst3EffectStateFiles.GetAutomaticPath(
                effect.Id,
                reference,
                _stateDirectory);
            try
            {
                var file = new FileInfo(statePath);
                if (!file.Exists || file.Length <= 0 || file.Length > MaximumPluginStateBytes)
                {
                    continue;
                }

                var state = File.ReadAllBytes(file.FullName);
                ValidatePluginState(effect, state);
                pluginStates[effect.Id] = state;
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                ArgumentException or
                NotSupportedException)
            {
                // Los presets v1 no prometían incluir el estado. Si el archivo auxiliar antiguo
                // ya no está o está corrupto, la identidad y el orden de la cadena aún se importan.
            }
        }

        return preset with
        {
            PluginStates = pluginStates.Count > 0 ? pluginStates : null
        };
    }

    private static AudioEffectChainPreset Normalize(AudioEffectChainPreset preset)
    {
        var name = string.IsNullOrWhiteSpace(preset.Name)
            ? "Cadena personal"
            : preset.Name.Trim();
        var effects = preset.Effects
            .Where(effect =>
                effect is
                {
                    Kind: AudioEffectKind.ExternalVst3,
                    ExternalVst3: not null
                })
            .Take(AudioEffectCatalog.MaximumSlots)
            .Select(AudioEffectSlotSetting.Normalize)
            .ToArray();
        var effectIds = effects
            .Select(effect => effect.Id)
            .ToHashSet(StringComparer.Ordinal);
        var pluginStates = preset.PluginStates?
            .Where(pair => effectIds.Contains(pair.Key) && pair.Value is { Length: > 0 })
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return preset with
        {
            Name = name,
            Effects = effects,
            PluginStates = pluginStates is { Count: > 0 } ? pluginStates : null
        };
    }

    private AudioEffectChainPreset ImportWithNewSlotIds(AudioEffectChainPreset preset)
    {
        var imports = preset.Effects
            .Select(effect => new ImportedEffect(
                effect,
                Guid.NewGuid().ToString("N"),
                preset.PluginStates?.GetValueOrDefault(effect.Id)))
            .ToArray();

        foreach (var import in imports)
        {
            ValidatePluginState(import.Effect, import.PluginState);
        }

        var materializedPaths = new List<string>();
        try
        {
            if (imports.Any(import => import.PluginState is { Length: > 0 }))
            {
                Directory.CreateDirectory(_stateDirectory);
            }

            var effects = new AudioEffectSlotSetting[imports.Length];
            var pluginStates = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            for (var index = 0; index < imports.Length; index++)
            {
                var import = imports[index];
                var reference = import.Effect.ExternalVst3!;
                if (import.PluginState is { Length: > 0 } state)
                {
                    var statePath = Path.Combine(
                        _stateDirectory,
                        $"imported-effect-{import.NewSlotId}.vstpreset");
                    WriteAllBytesAtomically(statePath, state);
                    materializedPaths.Add(statePath);
                    reference = reference with { PresetPath = statePath };
                    pluginStates.Add(import.NewSlotId, state);
                }

                effects[index] = import.Effect with
                {
                    Id = import.NewSlotId,
                    ExternalVst3 = reference
                };
            }

            return preset with
            {
                Effects = effects,
                PluginStates = pluginStates.Count > 0 ? pluginStates : null
            };
        }
        catch
        {
            foreach (var path in materializedPaths)
            {
                try
                {
                    File.Delete(path);
                }
                catch
                {
                }
            }
            throw;
        }
    }

    private static void ValidatePluginStates(
        IReadOnlyList<AudioEffectSlotSetting> effects,
        IReadOnlyDictionary<string, byte[]>? pluginStates)
    {
        if (pluginStates is null)
        {
            return;
        }

        foreach (var effect in effects)
        {
            if (pluginStates.TryGetValue(effect.Id, out var state))
            {
                ValidatePluginState(effect, state);
            }
        }
    }

    private static void ValidatePluginState(AudioEffectSlotSetting effect, byte[]? state)
    {
        if (state is not { Length: > 0 })
        {
            return;
        }
        if (state.LongLength > MaximumPluginStateBytes)
        {
            throw new InvalidDataException(
                $"El estado del slot {effect.Id} supera el tamaño máximo permitido.");
        }

        using var stream = new MemoryStream(state, writable: false);
        var preset = Vst3Preset.Read(stream);
        if (!string.Equals(
                preset.ClassId,
                effect.ExternalVst3!.ClassId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"El estado del slot {effect.Id} pertenece al plugin {preset.ClassId}, " +
                $"no a {effect.ExternalVst3.ClassId}.");
        }
    }

    private static void WriteAllBytesAtomically(string path, byte[] contents)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, contents);
            File.Move(temporary, path, overwrite: false);
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

    private sealed record PresetDocument(
        int SchemaVersion,
        string Name,
        IReadOnlyList<AudioEffectSlotSetting>? Effects,
        bool EffectsBypassed,
        IReadOnlyDictionary<string, byte[]>? PluginStates = null);

    private sealed record ImportedEffect(
        AudioEffectSlotSetting Effect,
        string NewSlotId,
        byte[]? PluginState);
}
