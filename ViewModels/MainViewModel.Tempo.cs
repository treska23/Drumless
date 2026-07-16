using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Midi;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
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
    private string _performanceResultText = "Sin sesión de precisión.";

    public RelayCommand AnalyzeTempoCommand { get; private set; } = null!;
    public RelayCommand StartPerformanceEvaluationCommand { get; private set; } = null!;
    public RelayCommand FinishPerformanceEvaluationCommand { get; private set; } = null!;

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
    public bool HasTempo => CurrentTrack?.Tempo is not null;
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

    private void InitializeTempoCommands()
    {
        AnalyzeTempoCommand = new RelayCommand(() => _ = AnalyzeCurrentTrackTempoAsync());
        StartPerformanceEvaluationCommand = new RelayCommand(StartPerformanceEvaluation);
        FinishPerformanceEvaluationCommand = new RelayCommand(() => FinishPerformanceEvaluation(false));
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
            SynchronizeTempoEditor(track);
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
        SynchronizeTempoEditor(track);
        OnPropertyChanged(nameof(CanAnalyzeTempo));
        OnPropertyChanged(nameof(HasTempo));
    }

    private void SynchronizeTempoEditor(LocalTrack? track)
    {
        _isSynchronizingTempo = true;
        try
        {
            var tempo = track?.Tempo;
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
            TempoStatus = tempo is null
                ? "Tempo sin analizar. Puedes detectarlo o introducirlo manualmente."
                : track!.TempoLabel;
            _audio.ConfigureMetronome(tempo);
        }
        finally
        {
            _isSynchronizingTempo = false;
        }
    }

    private void SaveTempoFromEditor()
    {
        if (_isSynchronizingTempo || CurrentTrack is null)
        {
            return;
        }

        var confidence = CurrentTrack.Tempo?.AnalysisConfidence ?? 0d;
        CurrentTrack.Tempo = new TempoSettings(
            TempoBpm,
            TempoFirstBeatSeconds,
            TempoBeatsPerBar,
            MetronomeEnabled,
            MetronomeVolume,
            confidence);
        _audio.ConfigureMetronome(CurrentTrack.Tempo);
        TempoStatus = CurrentTrack.TempoLabel;
        OnPropertyChanged(nameof(HasTempo));
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
        PerformanceResultText = result.TotalHits == 0
            ? "Sesión finalizada sin golpes MIDI registrados."
            : $"Precisión {result.AccuracyPercent:0.0}% · {result.AccurateHits}/{result.TotalHits} golpes " +
              $"dentro de ±{DrumPerformanceScorer.AccurateToleranceMilliseconds:0} ms · " +
              $"{result.EarlyHits} adelantados · {result.LateHits} atrasados · " +
              $"error medio {result.MeanAbsoluteErrorMilliseconds:0.0} ms" +
              (naturalEnd ? " · canción terminada" : string.Empty);
        OnPropertyChanged(nameof(IsPerformanceEvaluationActive));
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
