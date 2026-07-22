using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    public event EventHandler? SongEffectWindowRequested;
    public event EventHandler<SongIdentityRequestEventArgs>? SongIdentityRequired;

    private readonly SongEffectRecommendationService _songEffectRecommendation = new();
    private readonly Vst3EffectParameterProbeService _vstEffectParameterProbe = new();
    private CancellationTokenSource? _songEffectAnalysisCancellation;
    private SongEffectProfile? _proposedSongEffectProfile;
    private SongEffectProfile? _savedSongEffectProfile;
    private bool _isAnalyzingSongEffects;
    private readonly List<string> _songEffectApplyErrors = [];
    private string _songEffectStatus =
        "Carga una pista local para preparar guitarra (input 1) y voz (input 2).";

    public RelayCommand OpenSongEffectWindowCommand { get; private set; } = null!;
    public RelayCommand AnalyzeSongEffectsCommand { get; private set; } = null!;
    public RelayCommand CancelSongEffectAnalysisCommand { get; private set; } = null!;
    public RelayCommand ApplySongEffectProfileCommand { get; private set; } = null!;

    public SongEffectProfile? ProposedSongEffectProfile
    {
        get => _proposedSongEffectProfile;
        private set
        {
            if (SetProperty(ref _proposedSongEffectProfile, value))
            {
                OnPropertyChanged(nameof(HasProposedSongEffectProfile));
                ApplySongEffectProfileCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public SongEffectProfile? SavedSongEffectProfile
    {
        get => _savedSongEffectProfile;
        private set
        {
            if (SetProperty(ref _savedSongEffectProfile, value))
            {
                OnPropertyChanged(nameof(HasSavedSongEffectProfile));
            }
        }
    }

    public bool HasProposedSongEffectProfile => ProposedSongEffectProfile is not null;
    public bool HasSavedSongEffectProfile => SavedSongEffectProfile is not null;

    public bool IsAnalyzingSongEffects
    {
        get => _isAnalyzingSongEffects;
        private set
        {
            if (SetProperty(ref _isAnalyzingSongEffects, value))
            {
                OnPropertyChanged(nameof(CanAnalyzeSongEffects));
                AnalyzeSongEffectsCommand?.RaiseCanExecuteChanged();
                CancelSongEffectAnalysisCommand?.RaiseCanExecuteChanged();
                ApplySongEffectProfileCommand?.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanAnalyzeSongEffects =>
        CurrentTrack is { IsAvailable: true } && !IsAnalyzingSongEffects;

    public string SongEffectStatus
    {
        get => _songEffectStatus;
        private set => SetProperty(ref _songEffectStatus, value);
    }

    private void InitializeSongEffectCommands()
    {
        OpenSongEffectWindowCommand = new RelayCommand(
            () => SongEffectWindowRequested?.Invoke(this, EventArgs.Empty));
        AnalyzeSongEffectsCommand = new RelayCommand(
            () => _ = AnalyzeSongEffectsAsync(),
            () => CanAnalyzeSongEffects);
        CancelSongEffectAnalysisCommand = new RelayCommand(
            CancelSongEffectAnalysis,
            () => IsAnalyzingSongEffects);
        ApplySongEffectProfileCommand = new RelayCommand(
            ApplyProposedSongEffectProfile,
            () => ProposedSongEffectProfile is not null && !IsAnalyzingSongEffects);
    }

    private async Task AnalyzeSongEffectsAsync()
    {
        var track = CurrentTrack;
        if (track is not { IsAvailable: true })
        {
            SongEffectStatus = "Carga una pista local disponible antes de preparar sus efectos.";
            return;
        }

        if (!TryResolveSongIdentity(track, out var artist, out var songTitle))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _songEffectAnalysisCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            IsAnalyzingSongEffects = true;
            ProposedSongEffectProfile = null;
            SongEffectStatus = "Comprobando el catálogo de efectos VST3…";
            if (Vst3Effects.Count == 0 && !IsScanningVstEffects)
            {
                await ScanVstEffectsAsync();
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (Vst3Effects.Count == 0)
            {
                throw new InvalidOperationException(
                    "No hay efectos VST3 catalogados. Búscalos primero en Dispositivos.");
            }

            var mediaKey = $"local:{track.Id}";
            var analysis = _analysisDatabase.Get(mediaKey);
            var available = Vst3Effects
                .Select((effect, index) => new InstalledEffectDescriptor(
                    $"fx-{index + 1:D3}",
                    effect.EffectType,
                    effect.ToReference()))
                .ToArray();
            SongEffectStatus =
                "Investigando la producción y consultando el modelo local mediante Ollama…";
            var profile = await _songEffectRecommendation.RecommendAsync(
                new SongEffectRecommendationRequest(
                    mediaKey,
                    track.Title,
                    artist,
                    songTitle,
                    analysis?.Tempo?.Bpm,
                    analysis?.SongStructure?.Sections
                        .Select(section => section.Label)
                        .ToArray() ?? [],
                    available),
                cancellation.Token);
            if (CurrentTrack?.Id != track.Id)
            {
                return;
            }

            SongEffectStatus = "Buscando presets VST3 compatibles instalados…";
            profile = await Task.Run(
                () => ResolveInstalledEffectPresets(profile),
                cancellation.Token);
            SongEffectStatus = "Leyendo en procesos aislados los parámetros reales de cada plugin…";
            var parameterCatalog = await _vstEffectParameterProbe.ProbeAsync(
                profile.Guitar.Slots.Select(slot => slot.Effect)
                    .Concat(profile.Voice.Slots.Select(slot => slot.Effect)),
                cancellation.Token);
            SongEffectStatus =
                "Adaptando presets y parámetros VST3 al sonido de esta canción mediante Ollama…";
            profile = await _songEffectRecommendation.TuneParametersAsync(
                profile,
                parameterCatalog,
                cancellation.Token);
            ProposedSongEffectProfile = profile;
            SongEffectStatus =
                "Propuesta terminada. Revísala antes de aplicarla a input 1 e input 2.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SongEffectStatus = "Preparación de efectos cancelada.";
        }
        catch (Exception exception) when (exception is
            HttpRequestException or
            JsonException or
            InvalidDataException or
            InvalidOperationException or
            IOException or
            UnauthorizedAccessException or
            TaskCanceledException)
        {
            SongEffectStatus = $"No se pudo preparar el sonido: {exception.Message}";
        }
        finally
        {
            IsAnalyzingSongEffects = false;
            Interlocked.CompareExchange(ref _songEffectAnalysisCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private SongEffectProfile ResolveInstalledEffectPresets(SongEffectProfile profile) =>
        profile with
        {
            Guitar = ResolveInstalledEffectPresets(profile.Guitar),
            Voice = ResolveInstalledEffectPresets(profile.Voice)
        };

    private bool TryResolveSongIdentity(
        LocalTrack track,
        out string artist,
        out string songTitle)
    {
        var mediaKey = $"local:{track.Id}";
        var saved = _analysisDatabase.Get(mediaKey)?.SongEffectProfile;
        if (saved is not null &&
            !string.IsNullOrWhiteSpace(saved.Artist) &&
            !string.IsNullOrWhiteSpace(saved.SongTitle))
        {
            artist = saved.Artist;
            songTitle = saved.SongTitle;
            return true;
        }

        if (SongIdentityResolver.TryResolve(track.Title, out artist, out songTitle))
        {
            return true;
        }

        var request = new SongIdentityRequestEventArgs(
            suggestedArtist: string.Empty,
            suggestedSongTitle: songTitle);
        SongIdentityRequired?.Invoke(this, request);
        if (!request.IsConfirmed ||
            string.IsNullOrWhiteSpace(request.Artist) ||
            string.IsNullOrWhiteSpace(request.SongTitle))
        {
            artist = string.Empty;
            songTitle = string.Empty;
            SongEffectStatus =
                "Necesito el intérprete original y el nombre de la canción para buscar su sonido.";
            return false;
        }

        artist = request.Artist.Trim();
        songTitle = request.SongTitle.Trim();
        return true;
    }

    private SongInputEffectChain ResolveInstalledEffectPresets(SongInputEffectChain chain) =>
        chain with
        {
            Slots = chain.Slots.Select(slot =>
            {
                var preset = _vstPresetDiscovery.FindCompatibleEffectPreset(
                    slot.Effect,
                    slot.PresetHint);
                return preset is null
                    ? slot
                    : slot with { Effect = slot.Effect with { PresetPath = preset } };
            }).ToArray()
        };

    private void CancelSongEffectAnalysis() =>
        _songEffectAnalysisCancellation?.Cancel();

    private void ApplyProposedSongEffectProfile()
    {
        var profile = ProposedSongEffectProfile;
        if (profile is null || CurrentTrack is null ||
            !string.Equals(
                profile.MediaKey,
                $"local:{CurrentTrack.Id}",
                StringComparison.Ordinal))
        {
            SongEffectStatus = "La propuesta ya no corresponde a la pista cargada.";
            return;
        }

        _analysisDatabase.SetSongEffectProfile(profile.MediaKey, profile);
        SavedSongEffectProfile = profile;
        var applied = ApplySongEffectProfileToAvailableInputs(profile);
        SaveTrackWorkspace();
        SongEffectStatus = _songEffectApplyErrors.Count > 0
            ? "Configuración guardada, pero algunos plugins quedaron pendientes y la señal " +
              $"continúa sin ellos: {string.Join(" · ", _songEffectApplyErrors)}"
            : applied switch
        {
            2 => "Configuración guardada y aplicada a guitarra (input 1) y voz (input 2).",
            1 => "Configuración guardada. Sólo una de las dos entradas está disponible ahora.",
            _ => "Configuración guardada. Elige una salida ASIO con input 1 e input 2 para aplicarla."
        };
    }

    private int ApplySongEffectProfileToAvailableInputs(SongEffectProfile profile)
    {
        _songEffectApplyErrors.Clear();
        var applied = 0;
        applied += ApplySongEffectChain(
            profile.MediaKey,
            profile.Guitar,
            AudioInputProfileKind.GuitarDrive) ? 1 : 0;
        applied += ApplySongEffectChain(
            profile.MediaKey,
            profile.Voice,
            AudioInputProfileKind.Voice) ? 1 : 0;
        if (applied > 0)
        {
            RememberAudioInputMonitors();
            ScheduleSettingsSave();
        }
        return applied;
    }

    private bool ApplySongEffectChain(
        string mediaKey,
        SongInputEffectChain chain,
        AudioInputProfileKind profileKind)
    {
        var monitor = AudioInputMonitors.FirstOrDefault(
            item => item.ChannelIndex == chain.ChannelIndex);
        if (monitor is null)
        {
            return false;
        }

        monitor.PropertyChanged -= OnAudioInputMonitorPropertyChanged;
        try
        {
            monitor.Profile = profileKind;
            monitor.LoadEffects(
                chain.Slots.Select((slot, index) => AudioEffectSlotSetting.Normalize(
                    new AudioEffectSlotSetting(
                        CreateSongEffectSlotId(
                            mediaKey,
                            chain.ChannelIndex,
                            index,
                            slot.Effect),
                        AudioEffectKind.ExternalVst3,
                        IsEnabled: true,
                        Amount: 0.5d,
                        Mix: slot.Mix,
                        ExternalVst3: slot.Effect))),
                bypassed: false);
        }
        finally
        {
            monitor.PropertyChanged += OnAudioInputMonitorPropertyChanged;
        }

        if (monitor.IsEnabled)
        {
            try
            {
                _audio.SetAudioInputEffects(
                    monitor.ChannelIndex,
                    monitor.EffectSlots.Select(slot => slot.ToSetting()).ToArray(),
                    bypassed: false);
            }
            catch (Exception exception)
            {
                _songEffectApplyErrors.Add($"{monitor.DisplayName}: {exception.Message}");
            }
        }
        return true;
    }

    private static string CreateSongEffectSlotId(
        string mediaKey,
        int channelIndex,
        int slotIndex,
        Vst3EffectReference effect)
    {
        var identity = string.Join(
            "|",
            mediaKey.Trim(),
            channelIndex,
            slotIndex,
            effect.ModulePath.Trim(),
            effect.ClassId.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    private void RestoreSongEffectProfileForCurrentTrack(bool applyToInputs = true)
    {
        var mediaKey = CurrentTrack is null ? null : $"local:{CurrentTrack.Id}";
        SavedSongEffectProfile = _analysisDatabase.Get(mediaKey)?.SongEffectProfile;
        ProposedSongEffectProfile = null;
        OnPropertyChanged(nameof(CanAnalyzeSongEffects));
        AnalyzeSongEffectsCommand?.RaiseCanExecuteChanged();

        if (SavedSongEffectProfile is null)
        {
            SongEffectStatus = CurrentTrack is null
                ? "Carga una pista local para preparar guitarra (input 1) y voz (input 2)."
                : "Esta pista todavía no tiene una configuración de efectos guardada.";
            return;
        }

        if (applyToInputs)
        {
            var applied = ApplySongEffectProfileToAvailableInputs(SavedSongEffectProfile);
            SongEffectStatus = _songEffectApplyErrors.Count > 0
                ? "Configuración restaurada con plugins pendientes: " +
                  string.Join(" · ", _songEffectApplyErrors)
                : applied == 2
                ? "Configuración guardada restaurada para esta canción."
                : "Configuración guardada pendiente de disponer de los dos inputs ASIO.";
        }
    }

    private void ReapplySavedSongEffectsToInputs()
    {
        var profile = CurrentTrack is null
            ? null
            : _analysisDatabase.Get($"local:{CurrentTrack.Id}")?.SongEffectProfile;
        if (profile is not null)
        {
            ApplySongEffectProfileToAvailableInputs(profile);
        }
    }
}

public sealed class SongIdentityRequestEventArgs(
    string suggestedArtist,
    string suggestedSongTitle) : EventArgs
{
    public string Artist { get; set; } = suggestedArtist;
    public string SongTitle { get; set; } = suggestedSongTitle;
    public bool IsConfirmed { get; set; }
}
