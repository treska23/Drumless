using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Midi;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    public event EventHandler<YouTubeMetronomeRequest>? YouTubeMetronomeChanged;
    private readonly TempoAnalysisService _tempoAnalysis = new();
    private readonly DrumPerformanceScorer _performanceScorer = new();
    private CancellationTokenSource? _tempoAnalysisCancellation;
    private bool _isSynchronizingTempo;
    private bool _isAnalyzingTempo;
    private double _tempoBpm = 120d;
    private double _tempoFirstBeatSeconds;
    private int _tempoBeatsPerBar = 4;
    private bool _metronomeEnabled;
    private double _metronomeVolume = 0.55d;
    private string _tempoStatus = "Carga una pista local y analiza su tempo cuando quieras.";
    private double _performanceLatencyCompensationMs;
    private string _performanceHistoryText = "Sin sesiones guardadas para esta pista.";
    private string? _performanceMediaKey;
    private string _performanceResultText = "Sin sesión de precisión.";

    public RelayCommand AnalyzeTempoCommand { get; private set; } = null!;
    public RelayCommand StartPerformanceEvaluationCommand { get; private set; } = null!;
    public RelayCommand FinishPerformanceEvaluationCommand { get; private set; } = null!;
    public RelayCommand ClearAnalysisDataCommand { get; private set; } = null!;

    public bool IsAnalyzingTempo
    {
        get => _isAnalyzingTempo;
        private set
        {
            if (SetProperty(ref _isAnalyzingTempo, value))
            {
                OnPropertyChanged(nameof(CanAnalyzeTempo));
            }
        }
    }

    public bool CanAnalyzeTempo => CurrentTrack is { IsAvailable: true } && !IsAnalyzingTempo;
    public bool HasTempo => CurrentTrack?.Tempo is not null || _currentYouTubeItem?.Tempo is not null;
    public bool CanEvaluatePerformance => CurrentTrack?.Tempo is not null;
    public TempoSettings? CurrentYouTubeTempoSettings => _currentYouTubeItem?.Tempo;
    public bool IsPerformanceEvaluationActive => _performanceScorer.IsActive;

    public double TempoBpm
    {
        get => _tempoBpm;
        set
        {
            if (SetProperty(ref _tempoBpm, Math.Clamp(value, 40d, 240d)))
            {
                SaveTempoFromEditor();
            }
        }
    }

    public double TempoFirstBeatSeconds
    {
        get => _tempoFirstBeatSeconds;
        set
        {
            if (SetProperty(ref _tempoFirstBeatSeconds, Math.Max(0d, value)))
            {
                SaveTempoFromEditor();
            }
        }
    }

    public int TempoBeatsPerBar
    {
        get => _tempoBeatsPerBar;
        set
        {
            if (SetProperty(ref _tempoBeatsPerBar, Math.Clamp(value, 1, 12)))
            {
                SaveTempoFromEditor();
            }
        }
    }

    public bool MetronomeEnabled
    {
        get => _metronomeEnabled;
        set
        {
            if (SetProperty(ref _metronomeEnabled, value))
            {
                SaveTempoFromEditor();
            }
        }
    }

    public double MetronomeVolume
    {
        get => _metronomeVolume;
        set
        {
            if (SetProperty(ref _metronomeVolume, Math.Clamp(value, 0d, 1d)))
            {
                SaveTempoFromEditor();
            }
        }
    }

    public string TempoStatus
    {
        get => _tempoStatus;
        private set => SetProperty(ref _tempoStatus, value);
    }

    public double PerformanceLatencyCompensationMs
    {
        get => _performanceLatencyCompensationMs;
        set
        {
            if (SetProperty(
                    ref _performanceLatencyCompensationMs,
                    Math.Clamp(value, -500d, 500d)))
            {
                ScheduleSettingsSave();
            }
        }
    }

    public string PerformanceResultText
    {
        get => _performanceResultText;
        private set => SetProperty(ref _performanceResultText, value);
    }

    public string PerformanceHistoryText
    {
        get => _performanceHistoryText;
        private set => SetProperty(ref _performanceHistoryText, value);
    }

    private void InitializeTempoCommands()
    {
        AnalyzeTempoCommand = new RelayCommand(() => _ = AnalyzeCurrentTrackTempoAsync());
        StartPerformanceEvaluationCommand = new RelayCommand(StartPerformanceEvaluation);
        FinishPerformanceEvaluationCommand = new RelayCommand(() => FinishPerformanceEvaluation(false));
        ClearAnalysisDataCommand = new RelayCommand(ClearCurrentAnalysisData);
    }

    private async Task AnalyzeCurrentTrackTempoAsync()
    {
        var track = CurrentTrack;
        if (track is not { IsAvailable: true })
        {
            TempoStatus = "Carga una pista local disponible antes de analizarla.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _tempoAnalysisCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            IsAnalyzingTempo = true;
            TempoStatus = $"Analizando pulsos de {track.Title}…";
            var result = await _tempoAnalysis.AnalyzeAsync(track.Path, cancellation.Token);
            if (CurrentTrack?.Id != track.Id)
            {
                return;
            }

            track.Tempo = new TempoSettings(
                result.Bpm,
                result.FirstBeatSeconds,
                4,
                MetronomeEnabled,
                MetronomeVolume,
                result.Confidence);
            _analysisDatabase.SetTempo(
                $"local:{track.Id}",
                track.Tempo,
                TempoAnalysisOrigin.Automatic);
            SynchronizeTempoEditor(track.Tempo, track.TempoLabel);
            SaveTrackWorkspace();
            TempoStatus = $"Detectado: {result.Bpm:0.##} BPM · primer pulso " +
                          $"{result.FirstBeatSeconds:0.000} s · confianza {result.Confidence:P0}. " +
                          "Ajusta BPM o primer pulso si la canción cambia de tempo.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            TempoStatus = "Análisis de tempo cancelado.";
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidDataException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            TempoStatus = $"No se pudo detectar el tempo: {exception.Message}";
        }
        finally
        {
            IsAnalyzingTempo = false;
            Interlocked.CompareExchange(ref _tempoAnalysisCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void OnCurrentTrackChanged(LocalTrack? track)
    {
        if (_performanceScorer.IsActive)
        {
            FinishPerformanceEvaluation(false);
        }
        SynchronizeTempoEditor(track?.Tempo, track?.TempoLabel);
        YouTubeMetronomeChanged?.Invoke(this, new YouTubeMetronomeRequest(null));
        OnPropertyChanged(nameof(CanAnalyzeTempo));
        OnPropertyChanged(nameof(HasTempo));
        OnPropertyChanged(nameof(CanEvaluatePerformance));
        RefreshPerformanceHistory();
    }

    private void OnCurrentYouTubeChanged(PlaylistItem item)
    {
        item.Tempo = _analysisDatabase.GetTempo(item.MediaKey) ?? item.Tempo;
        SynchronizeTempoEditor(
            item.Tempo,
            item.Tempo is null
                ? null
                : $"YouTube · {item.Tempo.Bpm:0.##} BPM · primer pulso " +
                  $"{item.Tempo.FirstBeatSeconds:0.000} s");
        YouTubeMetronomeChanged?.Invoke(
            this,
            new YouTubeMetronomeRequest(item.Tempo));
        OnPropertyChanged(nameof(CanAnalyzeTempo));
        OnPropertyChanged(nameof(HasTempo));
        OnPropertyChanged(nameof(CanEvaluatePerformance));
        RefreshPerformanceHistory();
    }

    private void SynchronizeTempoEditor(TempoSettings? tempo, string? label)
    {
        _isSynchronizingTempo = true;
        try
        {
            _tempoBpm = tempo?.Bpm ?? 120d;
            _tempoFirstBeatSeconds = tempo?.FirstBeatSeconds ?? 0d;
            _tempoBeatsPerBar = tempo?.BeatsPerBar ?? 4;
            _metronomeEnabled = tempo?.MetronomeEnabled ?? false;
            _metronomeVolume = tempo?.MetronomeVolume ?? 0.55d;
            OnPropertyChanged(nameof(TempoBpm));
            OnPropertyChanged(nameof(TempoFirstBeatSeconds));
            OnPropertyChanged(nameof(TempoBeatsPerBar));
            OnPropertyChanged(nameof(MetronomeEnabled));
            OnPropertyChanged(nameof(MetronomeVolume));
            OnPropertyChanged(nameof(HasTempo));
            OnPropertyChanged(nameof(CanEvaluatePerformance));
            TempoStatus = tempo is null
                ? CurrentTrack is null && _currentYouTubeItem is not null
                    ? "YouTube: introduce BPM y primer pulso manualmente; el análisis automático solo usa archivos locales."
                    : "Tempo sin analizar. Puedes detectarlo o introducirlo manualmente."
                : label ?? $"{tempo.Bpm:0.##} BPM";
            _audio.ConfigureMetronome(CurrentTrack is null ? null : tempo);
        }
        finally
        {
            _isSynchronizingTempo = false;
        }
    }

    private void SaveTempoFromEditor()
    {
        if (_isSynchronizingTempo || (CurrentTrack is null && _currentYouTubeItem is null))
        {
            return;
        }

        var currentTempo = CurrentTrack?.Tempo ?? _currentYouTubeItem?.Tempo;
        var tempo = new TempoSettings(
            TempoBpm,
            TempoFirstBeatSeconds,
            TempoBeatsPerBar,
            MetronomeEnabled,
            MetronomeVolume,
            currentTempo?.AnalysisConfidence ?? 0d);
        if (CurrentTrack is not null)
        {
            CurrentTrack.Tempo = tempo;
            var mediaKey = $"local:{CurrentTrack.Id}";
            _analysisDatabase.SetTempo(mediaKey, tempo, ResolveEditedTempoOrigin(mediaKey));
            _audio.ConfigureMetronome(tempo);
            YouTubeMetronomeChanged?.Invoke(this, new YouTubeMetronomeRequest(null));
            TempoStatus = CurrentTrack.TempoLabel;
        }
        else
        {
            var mediaKey = _currentYouTubeItem!.MediaKey;
            _analysisDatabase.SetTempo(mediaKey, tempo, ResolveEditedTempoOrigin(mediaKey));
            ApplyYouTubeTempo(_currentYouTubeItem.YouTubeVideoId!, tempo);
            _audio.ConfigureMetronome(null);
            YouTubeMetronomeChanged?.Invoke(this, new YouTubeMetronomeRequest(tempo));
            TempoStatus = $"YouTube · {tempo.Bpm:0.##} BPM · primer pulso {tempo.FirstBeatSeconds:0.000} s";
        }
        OnPropertyChanged(nameof(HasTempo));
        OnPropertyChanged(nameof(CanEvaluatePerformance));
        ScheduleSettingsSave();
    }

    private void StartPerformanceEvaluation()
    {
        if (CurrentTrack?.Tempo is not { } tempo)
        {
            PerformanceResultText = "Analiza o introduce el tempo antes de evaluar la batería.";
            return;
        }

        _performanceScorer.Start(tempo, PerformanceLatencyCompensationMs);
        _performanceMediaKey = $"local:{CurrentTrack.Id}";
        PerformanceResultText = "Evaluación activa · toca la batería MIDI y reproduce la pista.";
        OnPropertyChanged(nameof(IsPerformanceEvaluationActive));
    }

    private void FinishPerformanceEvaluation(bool naturalEnd)
    {
        if (!_performanceScorer.IsActive)
        {
            return;
        }

        var result = _performanceScorer.Finish();
        if (!string.IsNullOrWhiteSpace(_performanceMediaKey))
        {
            _analysisDatabase.AddPerformanceSession(
                _performanceMediaKey,
                DrumPerformanceSession.Create(
                    result,
                    PerformanceLatencyCompensationMs,
                    naturalEnd));
            SaveTrackWorkspace(silent: true);
        }
        _performanceMediaKey = null;
        PerformanceResultText = result.TotalHits == 0
            ? "Sesión finalizada sin golpes MIDI registrados."
            : $"Precisión {result.AccuracyPercent:0.0}% · {result.AccurateHits}/{result.TotalHits} golpes " +
              $"dentro de ±{DrumPerformanceScorer.AccurateToleranceMilliseconds:0} ms · " +
              $"{result.EarlyHits} adelantados · {result.LateHits} atrasados · " +
              $"error medio {result.MeanAbsoluteErrorMilliseconds:0.0} ms" +
              (naturalEnd ? " · canción terminada" : string.Empty);
        OnPropertyChanged(nameof(IsPerformanceEvaluationActive));
        RefreshPerformanceHistory();
    }

    private TempoAnalysisOrigin ResolveEditedTempoOrigin(string mediaKey)
    {
        var existing = _analysisDatabase.Get(mediaKey);
        return existing?.TempoOrigin is TempoAnalysisOrigin.Automatic or
            TempoAnalysisOrigin.ManuallyAdjusted
            ? TempoAnalysisOrigin.ManuallyAdjusted
            : TempoAnalysisOrigin.Manual;
    }

    private void ApplyYouTubeTempo(string videoId, TempoSettings? tempo)
    {
        foreach (var item in Playlists.SelectMany(playlist => playlist.Items)
                     .Where(item => item.Kind == PlaylistItemKind.YouTube &&
                                    string.Equals(item.YouTubeVideoId, videoId, StringComparison.Ordinal)))
        {
            item.Tempo = tempo;
        }
    }

    private string? CurrentAnalysisMediaKey => CurrentTrack is not null
        ? $"local:{CurrentTrack.Id}"
        : _currentYouTubeItem?.MediaKey;

    private void RefreshPerformanceHistory()
    {
        var sessions = _analysisDatabase.Get(CurrentAnalysisMediaKey)?.PerformanceSessions ?? [];
        if (sessions.Count == 0)
        {
            PerformanceHistoryText = "Sin sesiones guardadas para esta pista.";
            return;
        }

        var latest = sessions
            .OrderByDescending(session => session.FinishedAtUtc)
            .Take(3)
            .Select(session =>
                $"{session.FinishedAtUtc.ToLocalTime():dd/MM/yyyy HH:mm} · " +
                $"{session.AccuracyPercent:0.0}% · {session.AccurateHits}/{session.TotalHits} golpes")
            .ToArray();
        PerformanceHistoryText = $"{sessions.Count} sesión(es) guardada(s)\n" +
                                 string.Join("\n", latest);
    }

    private void ClearCurrentAnalysisData()
    {
        if (_performanceScorer.IsActive)
        {
            FinishPerformanceEvaluation(false);
        }

        var mediaKey = CurrentAnalysisMediaKey;
        if (mediaKey is null || !_analysisDatabase.Remove(mediaKey))
        {
            TempoStatus = "Esta pista no tiene datos de análisis guardados.";
            return;
        }

        if (CurrentTrack is not null)
        {
            CurrentTrack.Tempo = null;
        }
        else if (_currentYouTubeItem?.YouTubeVideoId is { } videoId)
        {
            ApplyYouTubeTempo(videoId, null);
        }

        SynchronizeTempoEditor(null, null);
        PerformanceResultText = "Datos de análisis y sesiones borrados.";
        RefreshPerformanceHistory();
        SaveTrackWorkspace();
        TempoStatus = "Datos de tempo, claqueta y puntuaciones borrados para esta pista.";
    }

    private void RecordPerformanceHit(MidiNoteMessage message)
    {
        if (!_performanceScorer.IsActive || !_desiredTrackPlaying || CurrentTrack is null)
        {
            return;
        }

        _performanceScorer.Record(
            _audio.TrackPosition.TotalSeconds,
            message.Note,
            message.Velocity);
    }
}
