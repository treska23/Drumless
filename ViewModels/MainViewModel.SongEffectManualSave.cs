using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using NAudio.Vst3;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    private RelayCommand? _saveCurrentSongEffectConfigurationCommand;

    public RelayCommand SaveCurrentSongEffectConfigurationCommand =>
        _saveCurrentSongEffectConfigurationCommand ??= new RelayCommand(
            SaveCurrentSongEffectConfiguration);

    private void SaveCurrentSongEffectConfiguration()
    {
        var track = CurrentTrack;
        if (track is not { IsAvailable: true })
        {
            SongEffectStatus = "Carga una pista local antes de guardar su configuración de plugins.";
            AudioInputStatus = SongEffectStatus;
            return;
        }

        var instrumentMonitor = ResolveInstrumentMonitor();
        var voiceMonitor = ResolveVoiceMonitor(instrumentMonitor);
        if (instrumentMonitor is null && voiceMonitor is null)
        {
            SongEffectStatus =
                "No hay entradas de audio disponibles. Selecciona primero una salida ASIO con entradas.";
            AudioInputStatus = SongEffectStatus;
            return;
        }

        var mediaKey = $"local:{track.Id}";
        var previous = SavedSongEffectProfile ?? ProposedSongEffectProfile;
        var artist = previous?.Artist ?? string.Empty;
        var songTitle = previous?.SongTitle ?? track.Title;
        if (previous is null &&
            SongIdentityResolver.TryResolve(track.Title, out var resolvedArtist, out var resolvedTitle))
        {
            artist = resolvedArtist;
            songTitle = resolvedTitle;
        }

        var instrumentChain = CaptureManualChain(
            instrumentMonitor,
            fallbackChannelIndex: 0,
            fallbackInstrument: "Instrumento / guitarra");
        var voiceChain = CaptureManualChain(
            voiceMonitor,
            fallbackChannelIndex: 1,
            fallbackInstrument: "Voz");
        var pluginCount = instrumentChain.Slots.Count + voiceChain.Slots.Count;
        var profile = new SongEffectProfile(
            previous?.Id ?? Guid.NewGuid().ToString("N"),
            mediaKey,
            $"{track.Title} · configuración manual",
            track.Title,
            artist,
            songTitle,
            DateTimeOffset.UtcNow,
            "manual",
            pluginCount == 0
                ? "Configuración manual sin plugins activos."
                : $"Configuración manual guardada: {instrumentChain.Slots.Count} plugin(s) de instrumento y " +
                  $"{voiceChain.Slots.Count} de voz.",
            instrumentChain,
            voiceChain);

        _analysisDatabase.SetSongEffectProfile(mediaKey, profile);
        SavedSongEffectProfile = profile;
        ProposedSongEffectProfile = null;
        RememberAudioInputMonitors();
        SaveTrackWorkspace();

        SongEffectStatus = pluginCount == 0
            ? $"Configuración sin plugins guardada para «{track.Title}»."
            : $"Configuración actual de {pluginCount} plugin(s) guardada para «{track.Title}».";
        AudioInputStatus = SongEffectStatus;
        StatusMessage = SongEffectStatus;
    }

    private AudioInputMonitorItem? ResolveInstrumentMonitor() =>
        AudioInputMonitors.FirstOrDefault(monitor => monitor.ChannelIndex == 0) ??
        AudioInputMonitors.FirstOrDefault(monitor => monitor.Profile is
            AudioInputProfileKind.GuitarClean or
            AudioInputProfileKind.GuitarDrive or
            AudioInputProfileKind.Bass or
            AudioInputProfileKind.Drums) ??
        AudioInputMonitors.FirstOrDefault(monitor => monitor.Profile != AudioInputProfileKind.Voice) ??
        AudioInputMonitors.FirstOrDefault();

    private AudioInputMonitorItem? ResolveVoiceMonitor(AudioInputMonitorItem? instrumentMonitor) =>
        AudioInputMonitors.FirstOrDefault(monitor =>
            monitor.ChannelIndex == 1 && !ReferenceEquals(monitor, instrumentMonitor)) ??
        AudioInputMonitors.FirstOrDefault(monitor =>
            monitor.Profile == AudioInputProfileKind.Voice &&
            !ReferenceEquals(monitor, instrumentMonitor)) ??
        AudioInputMonitors.FirstOrDefault(monitor => !ReferenceEquals(monitor, instrumentMonitor));

    private static SongInputEffectChain CaptureManualChain(
        AudioInputMonitorItem? monitor,
        int fallbackChannelIndex,
        string fallbackInstrument)
    {
        if (monitor is null)
        {
            return new SongInputEffectChain(
                fallbackChannelIndex,
                fallbackInstrument,
                "Sin entrada disponible al guardar.",
                []);
        }

        SongEffectSlotRecommendation[] slots = monitor.EffectsBypassed
            ? []
            : monitor.EffectSlots
                .Where(slot => slot.IsEnabled && slot.ExternalVst3 is not null)
                .Select(slot =>
                {
                    var effect = ResolveStateBackedReference(slot);
                    var effectType = !string.IsNullOrWhiteSpace(effect.SubCategories)
                        ? effect.SubCategories
                        : !string.IsNullOrWhiteSpace(effect.Category)
                            ? effect.Category
                            : "VST3";
                    var presetHint = string.IsNullOrWhiteSpace(effect.PresetPath)
                        ? string.Empty
                        : Path.GetFileNameWithoutExtension(effect.PresetPath);
                    return new SongEffectSlotRecommendation(
                        effect,
                        effectType,
                        "Ajuste manual guardado por el usuario",
                        presetHint,
                        slot.Mix);
                })
                .ToArray();

        var profileLabel = AudioInputProfileCatalog.Get(monitor.Profile).Label;
        return new SongInputEffectChain(
            monitor.ChannelIndex,
            profileLabel,
            monitor.EffectsBypassed
                ? "Cadena guardada en bypass; se restaurará como señal sin plugins activos."
                : $"Cadena manual de {monitor.DisplayName}.",
            slots);
    }

    private static Vst3EffectReference ResolveStateBackedReference(AudioEffectSlotItem slot)
    {
        var effect = slot.ExternalVst3!;
        var automaticStatePath = Vst3EffectStateFiles.GetAutomaticPath(slot.Id, effect);
        return File.Exists(automaticStatePath)
            ? effect with { PresetPath = automaticStatePath }
            : effect;
    }

    private static IReadOnlyDictionary<string, byte[]> CaptureEffectStates(
        IReadOnlyList<AudioEffectSlotSetting> effects,
        Func<string, byte[]?> captureLiveState)
    {
        const long maximumStateBytes = 64L * 1024 * 1024;
        var states = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var effect in effects)
        {
            if (effect.ExternalVst3 is not { } reference)
            {
                continue;
            }

            var liveState = captureLiveState(effect.Id);
            if (IsCompatibleVstPreset(
                    liveState,
                    reference.ClassId,
                    maximumStateBytes))
            {
                states[effect.Id] = liveState!;
                continue;
            }

            var automaticStatePath = Vst3EffectStateFiles.GetAutomaticPath(effect.Id, reference);
            var storedState = TryReadCompatibleVstPreset(
                                  automaticStatePath,
                                  reference.ClassId,
                                  maximumStateBytes) ??
                              TryReadCompatibleVstPreset(
                                  reference.PresetPath,
                                  reference.ClassId,
                                  maximumStateBytes);
            if (storedState is not null)
            {
                states[effect.Id] = storedState;
            }
        }
        return states;
    }

    private static byte[]? TryReadCompatibleVstPreset(
        string? path,
        string classId,
        long maximumStateBytes)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length <= 0 || file.Length > maximumStateBytes)
            {
                return null;
            }
            var state = File.ReadAllBytes(file.FullName);
            return IsCompatibleVstPreset(state, classId, maximumStateBytes)
                ? state
                : null;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException or
            NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsCompatibleVstPreset(
        byte[]? state,
        string classId,
        long maximumStateBytes)
    {
        if (state is not { Length: > 0 } || state.LongLength > maximumStateBytes)
        {
            return false;
        }
        try
        {
            using var stream = new MemoryStream(state, writable: false);
            return string.Equals(
                Vst3Preset.Read(stream).ClassId,
                classId,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidDataException or
            ArgumentException or
            NotSupportedException)
        {
            return false;
        }
    }
}
