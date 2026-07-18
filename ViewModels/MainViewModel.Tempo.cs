using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Midi;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using Microsoft.Win32;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    public event EventHandler<YouTubeMetronomeRequest>? YouTubeMetronomeChanged;
    private readonly TempoAnalysisService _tempoAnalysis = new();
    private readonly TempoSourceSearchService _tempoSourceSearch = new();
    private readonly DrumReferenceAnalysisService _drumReferenceAnalysis = new();
    private readonly DrumPerformanceScorer _performanceScorer = new();
    private CancellationTokenSource? _tempoAnalysisCancellation;
    private CancellationTokenSource? _tempoSourceCancellation;
    private bool _isSynchronizingTempo;
    private bool _isAnalyzingTempo;
    private bool _isSearchingTempoSources;
    private bool _isContrastingTempoSources;
    private bool _isAnalyzingDrumReference;
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
    private double _activePerformanceLatencyCompensationMs;
    private double _youtubePerformancePositionSeconds;
    private TempoSegmentEditorItem? _selectedTempoSegment;
    private TempoSourceCandidate? _selectedTempoSourceCandidate;
    private string _drumReferenceStatus = "Sin referencia de batería; la puntuación usa sólo la rejilla.";

    public RelayCommand AnalyzeTempoCommand { get; private set; } = null!;
    public RelayCommand ApplyTempoProposalCommand { get; private set; } = null!;
    public RelayCommand AddTempoSegmentCommand { get; private set; } = null!;
    public RelayCommand RemoveTempoSegmentCommand { get; private set; } = null!;
    public RelayCommand MoveTempoSegmentUpCommand { get; private set; } = null!;
    public RelayCommand MoveTempoSegmentDownCommand { get; private set; } = null!;
    public RelayCommand SearchTempoSourcesCommand { get; private set; } = null!;
    public RelayCommand ContrastTempoSourcesCommand { get; private set; } = null!;
    public RelayCommand ApplyTempoSourceCommand { get; private set; } = null!;
    public RelayCommand OpenTempoSourceCommand { get; private set; } = null!;
    public RelayCommand AnalyzeDrumReferenceCommand { get; private set; } = null!;
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
    public bool CanEvaluatePerformance =>
        CurrentTrack?.Tempo is not null || _currentYouTubeItem?.Tempo is not null;
    public TempoSettings? CurrentYouTubeTempoSettings => _currentYouTubeItem?.Tempo;
    public bool IsPerformanceEvaluationActive => _performanceScorer.IsActive;
    public bool CanAnalyzeDrumReference => CurrentTrack is { IsAvailable: true } &&
                                           !IsAnalyzingDrumReference;
    public ObservableCollection<TempoSegmentEditorItem> TempoSegments { get; } = [];
    public ObservableCollection<TempoSegmentEditorItem> ProposedTempoSegments { get; } = [];
    public ObservableCollection<TempoSourceCandidate> TempoSourceCandidates { get; } = [];
    public bool HasTempoSegments => TempoSegments.Count > 0;
    public bool HasTempoProposal => ProposedTempoSegments.Count > 0;
    public bool HasTempoSourceCandidates => TempoSourceCandidates.Count > 0;

    public bool IsSearchingTempoSources
    {
        get => _isSearchingTempoSources;
        private set => SetProperty(ref _isSearchingTempoSources, value);
    }

    public bool IsContrastingTempoSources
    {
        get => _isContrastingTempoSources;
        private set => SetProperty(ref _isContrastingTempoSources, value);
    }

    public bool IsAnalyzingDrumReference
    {
        get => _isAnalyzingDrumReference;
        private set
        {
            if (SetProperty(ref _isAnalyzingDrumReference, value))
            {
                OnPropertyChanged(nameof(CanAnalyzeDrumReference));
            }
        }
    }

    public string DrumReferenceStatus
    {
        get => _drumReferenceStatus;
        private set => SetProperty(ref _drumReferenceStatus, value);
    }

    public TempoSegmentEditorItem? SelectedTempoSegment
    {
        get => _selectedTempoSegment;
        set
        {
            if (SetProperty(ref _selectedTempoSegment, value) && value is not null)
            {
                LoadSelectedTempoSegment(value);
            }
        }
    }

    public TempoSourceCandidate? SelectedTempoSourceCandidate
    {
        get => _selectedTempoSourceCandidate;
        set => SetProperty(ref _selectedTempoSourceCandidate, value);
    }

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
        ApplyTempoProposalCommand = new RelayCommand(ApplyTempoProposal);
        AddTempoSegmentCommand = new RelayCommand(AddTempoSegment);
        RemoveTempoSegmentCommand = new RelayCommand(RemoveTempoSegment);
        MoveTempoSegmentUpCommand = new RelayCommand(() => MoveTempoSegment(-1));
        MoveTempoSegmentDownCommand = new RelayCommand(() => MoveTempoSegment(1));
        SearchTempoSourcesCommand = new RelayCommand(() => _ = SearchTempoSourcesAsync());
        ContrastTempoSourcesCommand = new RelayCommand(() => _ = ContrastTempoSourcesAsync());
        ApplyTempoSourceCommand = new RelayCommand(ApplySelectedTempoSource);
        OpenTempoSourceCommand = new RelayCommand(OpenSelectedTempoSource);
        AnalyzeDrumReferenceCommand = new RelayCommand(
            () => _ = AnalyzeDrumReferenceAsync());
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
            TempoStatus = $"Analizando {track.Title} por ventanas…";
            var result = await _tempoAnalysis.AnalyzeMapAsync(track.Path, cancellation.Token);
            if (CurrentTrack?.Id != track.Id)
            {
                return;
            }

            ReplaceEditorCollection(ProposedTempoSegments, result.Segments, subscribe: false);
            OnPropertyChanged(nameof(HasTempoProposal));
            TempoStatus = $"{result.Summary} Confianza global {result.OverallConfidence:P0}. " +
                          "La propuesta no se aplicará hasta que pulses «Aplicar propuesta».";
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

    private void ApplyTempoProposal()
    {
        if (ProposedTempoSegments.Count == 0 ||
            (CurrentTrack is null && _currentYouTubeItem is null))
        {
            TempoStatus = "No hay una propuesta de mapa pendiente.";
            return;
        }

        var segments = ProposedTempoSegments.Select(item => item.ToModel()).ToArray();
        var first = segments[0];
        var tempo = TempoSettings.Normalize(new TempoSettings(
            first.Bpm,
            first.FirstBeatSeconds,
            first.BeatsPerBar,
            MetronomeEnabled,
            MetronomeVolume,
            first.Confidence,
            segments));
        ApplyTempoToCurrent(tempo, TempoAnalysisOrigin.Automatic);
        ProposedTempoSegments.Clear();
        OnPropertyChanged(nameof(HasTempoProposal));
        TempoStatus = $"Mapa aplicado: {tempo.EffectiveSegments.Count} tramo(s). " +
                      "Puedes ajustar cada límite, BPM y primer pulso.";
    }

    private void AddTempoSegment()
    {
        if (CurrentTrack is null && _currentYouTubeItem is null)
        {
            TempoStatus = "Carga una pista o vídeo antes de crear un tramo.";
            return;
        }

        var selected = SelectedTempoSegment;
        var start = selected is null
            ? Math.Max(0d, TrackProgress)
            : Math.Max(selected.StartSeconds + 1d, TrackProgress);
        var bpm = selected?.Bpm ?? TempoBpm;
        var beatSeconds = 60d / bpm;
        var anchor = selected is null
            ? Math.Max(0d, TempoFirstBeatSeconds)
            : selected.FirstBeatSeconds +
              Math.Ceiling((start - selected.FirstBeatSeconds) / beatSeconds) * beatSeconds;
        var item = new TempoSegmentEditorItem(TempoSegment.Create(
            start,
            bpm,
            Math.Max(0d, anchor),
            selected?.BeatsPerBar ?? TempoBeatsPerBar,
            sourceName: "Edición manual"));
        item.PropertyChanged += OnTempoSegmentEditorPropertyChanged;
        TempoSegments.Add(item);
        SortTempoSegments(item);
        SelectedTempoSegment = item;
        SaveTempoFromSegments(TempoAnalysisOrigin.ManuallyAdjusted);
    }

    private void RemoveTempoSegment()
    {
        var selected = SelectedTempoSegment;
        if (selected is null)
        {
            return;
        }
        if (TempoSegments.Count == 1)
        {
            TempoStatus = "El mapa necesita al menos un tramo.";
            return;
        }

        var index = TempoSegments.IndexOf(selected);
        selected.PropertyChanged -= OnTempoSegmentEditorPropertyChanged;
        TempoSegments.Remove(selected);
        SelectedTempoSegment = TempoSegments[Math.Clamp(index, 0, TempoSegments.Count - 1)];
        OnPropertyChanged(nameof(HasTempoSegments));
        SaveTempoFromSegments(TempoAnalysisOrigin.ManuallyAdjusted);
    }

    private void MoveTempoSegment(int direction)
    {
        var selected = SelectedTempoSegment;
        if (selected is null || direction == 0)
        {
            return;
        }
        var index = TempoSegments.IndexOf(selected);
        var target = Math.Clamp(index + Math.Sign(direction), 0, TempoSegments.Count - 1);
        if (target == index)
        {
            return;
        }

        var other = TempoSegments[target];
        (selected.StartSeconds, other.StartSeconds) = (other.StartSeconds, selected.StartSeconds);
        TempoSegments.Move(index, target);
        SaveTempoFromSegments(TempoAnalysisOrigin.ManuallyAdjusted);
    }

    private async Task SearchTempoSourcesAsync()
    {
        var title = CurrentTrack?.Title ?? _currentYouTubeItem?.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            TempoStatus = "Carga una pista o vídeo para buscar fuentes de tempo.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _tempoSourceCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            IsSearchingTempoSources = true;
            TempoStatus = $"Buscando fuentes verificables para «{title}»…";
            var candidates = await _tempoSourceSearch.SearchAsync(title, cancellation.Token);
            if (!string.Equals(
                    CurrentTrack?.Title ?? _currentYouTubeItem?.Title,
                    title,
                    StringComparison.Ordinal))
            {
                return;
            }

            TempoSourceCandidates.Clear();
            foreach (var candidate in candidates)
            {
                TempoSourceCandidates.Add(candidate);
            }
            SelectedTempoSourceCandidate = TempoSourceCandidates.FirstOrDefault();
            OnPropertyChanged(nameof(HasTempoSourceCandidates));
            TempoStatus = candidates.Count == 0
                ? "No se encontraron resultados que mostrasen un BPM explícito. Puedes editar el mapa manualmente."
                : $"{candidates.Count} candidato(s) con URL y texto de fuente. " +
                  "Selecciona uno para aplicarlo; ninguno se aplica automáticamente.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            TempoStatus = "Búsqueda de tempo cancelada.";
        }
        catch (Exception exception) when (exception is
            HttpRequestException or
            IOException or
            InvalidDataException or
            TaskCanceledException)
        {
            TempoStatus = $"No se pudieron consultar fuentes: {exception.Message}";
        }
        finally
        {
            IsSearchingTempoSources = false;
            Interlocked.CompareExchange(ref _tempoSourceCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private async Task ContrastTempoSourcesAsync()
    {
        if (TempoSourceCandidates.Count == 0)
        {
            TempoStatus = "Busca primero candidatos con fuente.";
            return;
        }

        try
        {
            IsContrastingTempoSources = true;
            TempoStatus = "Ollama está contrastando únicamente los candidatos con fuente…";
            var contrasted = await _tempoSourceSearch.ContrastWithOllamaAsync(
                TempoSourceCandidates.ToArray());
            TempoSourceCandidates.Clear();
            foreach (var candidate in contrasted)
            {
                TempoSourceCandidates.Add(candidate);
            }
            SelectedTempoSourceCandidate = TempoSourceCandidates.FirstOrDefault();
            TempoStatus = "Contraste local terminado. Ollama sólo ha ordenado y comentado fuentes existentes.";
        }
        catch (Exception exception) when (exception is
            HttpRequestException or
            IOException or
            InvalidDataException or
            InvalidOperationException or
            TaskCanceledException)
        {
            TempoStatus = $"Ollama no está disponible: {exception.Message}. Las fuentes web siguen intactas.";
        }
        finally
        {
            IsContrastingTempoSources = false;
        }
    }

    private void ApplySelectedTempoSource()
    {
        var candidate = SelectedTempoSourceCandidate;
        if (candidate is null || (CurrentTrack is null && _currentYouTubeItem is null))
        {
            TempoStatus = "Selecciona un candidato con fuente antes de aplicarlo.";
            return;
        }

        if (TempoSegments.Count == 0)
        {
            var segment = new TempoSegmentEditorItem(TempoSegment.Create(
                0d,
                candidate.Bpm,
                TempoFirstBeatSeconds,
                TempoBeatsPerBar,
                candidate.Confidence,
                candidate.SourceName,
                candidate.SourceUrl));
            segment.PropertyChanged += OnTempoSegmentEditorPropertyChanged;
            TempoSegments.Add(segment);
            SelectedTempoSegment = segment;
        }
        else
        {
            var segment = SelectedTempoSegment ?? TempoSegments[0];
            _isSynchronizingTempo = true;
            try
            {
                segment.Bpm = candidate.Bpm;
                segment.Confidence = candidate.Confidence;
                segment.SourceName = candidate.SourceName;
                segment.SourceUrl = candidate.SourceUrl;
            }
            finally
            {
                _isSynchronizingTempo = false;
            }
        }

        SaveTempoFromSegments(TempoAnalysisOrigin.OnlineSource);
        TempoStatus = $"{candidate.Bpm:0.##} BPM aplicado al tramo seleccionado desde " +
                      $"{candidate.SourceName}. La URL queda guardada con el mapa.";
    }

    private void OpenSelectedTempoSource()
    {
        var url = SelectedTempoSourceCandidate?.SourceUrl ??
                  SelectedTempoSegment?.SourceUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            TempoStatus = "El tramo seleccionado no tiene una URL de fuente válida.";
            return;
        }

        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private async Task AnalyzeDrumReferenceAsync()
    {
        var current = CurrentTrack;
        if (current is not { IsAvailable: true })
        {
            DrumReferenceStatus = "Carga una pista local antes de elegir la referencia.";
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Elegir pista de referencia de batería",
            Filter = "Audio|*.wav;*.mp3;*.flac;*.aiff;*.aif;*.m4a;*.wma|Todos los archivos|*.*",
            FileName = current.Path,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            IsAnalyzingDrumReference = true;
            DrumReferenceStatus = "Detectando golpes en la referencia seleccionada…";
            var reference = await _drumReferenceAnalysis.AnalyzeAsync(dialog.FileName);
            if (CurrentTrack?.Id != current.Id)
            {
                return;
            }
            _analysisDatabase.SetDrumReference($"local:{current.Id}", reference);
            SaveTrackWorkspace();
            DrumReferenceStatus =
                $"{reference.HitTimesSeconds.Count} golpes de referencia · " +
                $"confianza {reference.Confidence:P0} · versión {reference.Version}.";
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidDataException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            DrumReferenceStatus = $"No se pudo analizar la referencia: {exception.Message}";
        }
        finally
        {
            IsAnalyzingDrumReference = false;
        }
    }

    private void OnCurrentTrackChanged(LocalTrack? track)
    {
        if (_performanceScorer.IsActive)
        {
            FinishPerformanceEvaluation(false);
        }
        ClearTransientTempoResults();
        SynchronizeTempoEditor(track?.Tempo, track?.TempoLabel);
        RefreshDrumReferenceStatus();
        YouTubeMetronomeChanged?.Invoke(this, new YouTubeMetronomeRequest(null));
        OnPropertyChanged(nameof(CanAnalyzeTempo));
        OnPropertyChanged(nameof(HasTempo));
        OnPropertyChanged(nameof(CanEvaluatePerformance));
        RefreshPerformanceHistory();
    }

    private void OnCurrentYouTubeChanged(PlaylistItem item)
    {
        ClearTransientTempoResults();
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
        RefreshDrumReferenceStatus();
    }

    private void SynchronizeTempoEditor(TempoSettings? tempo, string? label)
    {
        _isSynchronizingTempo = true;
        try
        {
            var normalized = tempo is null ? null : TempoSettings.Normalize(tempo);
            ReplaceEditorCollection(
                TempoSegments,
                normalized?.EffectiveSegments ?? [],
                subscribe: true);
            _selectedTempoSegment = TempoSegments.FirstOrDefault();
            var selected = _selectedTempoSegment;
            _tempoBpm = selected?.Bpm ?? 120d;
            _tempoFirstBeatSeconds = selected?.FirstBeatSeconds ?? 0d;
            _tempoBeatsPerBar = selected?.BeatsPerBar ?? 4;
            _metronomeEnabled = tempo?.MetronomeEnabled ?? false;
            _metronomeVolume = tempo?.MetronomeVolume ?? 0.55d;
            OnPropertyChanged(nameof(SelectedTempoSegment));
            OnPropertyChanged(nameof(TempoBpm));
            OnPropertyChanged(nameof(TempoFirstBeatSeconds));
            OnPropertyChanged(nameof(TempoBeatsPerBar));
            OnPropertyChanged(nameof(MetronomeEnabled));
            OnPropertyChanged(nameof(MetronomeVolume));
            OnPropertyChanged(nameof(HasTempo));
            OnPropertyChanged(nameof(HasTempoSegments));
            OnPropertyChanged(nameof(CanEvaluatePerformance));
            TempoStatus = tempo is null
                ? CurrentTrack is null && _currentYouTubeItem is not null
                    ? "YouTube: crea el mapa manualmente o busca BPM en fuentes verificables."
                    : "Tempo sin analizar. Analiza para obtener una propuesta o consulta fuentes."
                : (label ?? $"{tempo.Bpm:0.##} BPM") +
                  $" · {normalized!.EffectiveSegments.Count} tramo(s)";
            _audio.ConfigureMetronome(CurrentTrack is null ? null : normalized);
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

        if (SelectedTempoSegment is null)
        {
            var item = new TempoSegmentEditorItem(TempoSegment.Create(
                0d,
                TempoBpm,
                TempoFirstBeatSeconds,
                TempoBeatsPerBar,
                sourceName: "Edición manual"));
            item.PropertyChanged += OnTempoSegmentEditorPropertyChanged;
            TempoSegments.Add(item);
            _selectedTempoSegment = item;
            OnPropertyChanged(nameof(SelectedTempoSegment));
            OnPropertyChanged(nameof(HasTempoSegments));
        }
        else
        {
            _isSynchronizingTempo = true;
            try
            {
                SelectedTempoSegment.Bpm = TempoBpm;
                SelectedTempoSegment.FirstBeatSeconds = TempoFirstBeatSeconds;
                SelectedTempoSegment.BeatsPerBar = TempoBeatsPerBar;
                if (string.IsNullOrWhiteSpace(SelectedTempoSegment.SourceName))
                {
                    SelectedTempoSegment.SourceName = "Edición manual";
                }
            }
            finally
            {
                _isSynchronizingTempo = false;
            }
        }

        SaveTempoFromSegments(ResolveEditedTempoOrigin(CurrentAnalysisMediaKey!));
    }

    private void SaveTempoFromSegments(TempoAnalysisOrigin origin)
    {
        if (_isSynchronizingTempo ||
            TempoSegments.Count == 0 ||
            (CurrentTrack is null && _currentYouTubeItem is null))
        {
            return;
        }

        var segments = TempoSegments
            .Select(item => item.ToModel())
            .OrderBy(segment => segment.StartSeconds)
            .ToArray();
        var first = segments[0];
        var tempo = TempoSettings.Normalize(new TempoSettings(
            first.Bpm,
            first.FirstBeatSeconds,
            first.BeatsPerBar,
            MetronomeEnabled,
            MetronomeVolume,
            first.Confidence,
            segments));
        ApplyTempoToCurrent(tempo, origin);
    }

    private void ApplyTempoToCurrent(TempoSettings tempo, TempoAnalysisOrigin origin)
    {
        tempo = TempoSettings.Normalize(tempo);
        if (CurrentTrack is not null)
        {
            CurrentTrack.Tempo = tempo;
            var mediaKey = $"local:{CurrentTrack.Id}";
            _analysisDatabase.SetTempo(mediaKey, tempo, origin);
            _audio.ConfigureMetronome(tempo);
            YouTubeMetronomeChanged?.Invoke(this, new YouTubeMetronomeRequest(null));
            TempoStatus = CurrentTrack.TempoLabel;
        }
        else
        {
            var mediaKey = _currentYouTubeItem!.MediaKey;
            _analysisDatabase.SetTempo(mediaKey, tempo, origin);
            ApplyYouTubeTempo(_currentYouTubeItem.YouTubeVideoId!, tempo);
            _audio.ConfigureMetronome(null);
            YouTubeMetronomeChanged?.Invoke(this, new YouTubeMetronomeRequest(tempo));
            TempoStatus = $"YouTube · {tempo.Bpm:0.##} BPM · primer pulso {tempo.FirstBeatSeconds:0.000} s";
        }
        OnPropertyChanged(nameof(HasTempo));
        OnPropertyChanged(nameof(CanEvaluatePerformance));
        ScheduleSettingsSave();
    }

    private void LoadSelectedTempoSegment(TempoSegmentEditorItem segment)
    {
        _isSynchronizingTempo = true;
        try
        {
            _tempoBpm = segment.Bpm;
            _tempoFirstBeatSeconds = segment.FirstBeatSeconds;
            _tempoBeatsPerBar = segment.BeatsPerBar;
            OnPropertyChanged(nameof(TempoBpm));
            OnPropertyChanged(nameof(TempoFirstBeatSeconds));
            OnPropertyChanged(nameof(TempoBeatsPerBar));
        }
        finally
        {
            _isSynchronizingTempo = false;
        }
    }

    private void OnTempoSegmentEditorPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (_isSynchronizingTempo || sender is not TempoSegmentEditorItem item)
        {
            return;
        }

        if (ReferenceEquals(item, SelectedTempoSegment))
        {
            LoadSelectedTempoSegment(item);
        }
        if (eventArgs.PropertyName == nameof(TempoSegmentEditorItem.StartSeconds))
        {
            SortTempoSegments(item);
        }
        SaveTempoFromSegments(TempoAnalysisOrigin.ManuallyAdjusted);
    }

    private void SortTempoSegments(TempoSegmentEditorItem selected)
    {
        var ordered = TempoSegments.OrderBy(item => item.StartSeconds).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var current = TempoSegments.IndexOf(ordered[index]);
            if (current != index)
            {
                TempoSegments.Move(current, index);
            }
        }
        SelectedTempoSegment = selected;
    }

    private void ReplaceEditorCollection(
        ObservableCollection<TempoSegmentEditorItem> destination,
        IEnumerable<TempoSegment> segments,
        bool subscribe)
    {
        foreach (var item in destination)
        {
            if (subscribe)
            {
                item.PropertyChanged -= OnTempoSegmentEditorPropertyChanged;
            }
        }
        destination.Clear();
        foreach (var segment in segments)
        {
            var item = new TempoSegmentEditorItem(segment);
            if (subscribe)
            {
                item.PropertyChanged += OnTempoSegmentEditorPropertyChanged;
            }
            destination.Add(item);
        }
    }

    private void ClearTransientTempoResults()
    {
        ProposedTempoSegments.Clear();
        TempoSourceCandidates.Clear();
        SelectedTempoSourceCandidate = null;
        OnPropertyChanged(nameof(HasTempoProposal));
        OnPropertyChanged(nameof(HasTempoSourceCandidates));
    }

    private void StartPerformanceEvaluation()
    {
        var tempo = CurrentTrack?.Tempo ?? _currentYouTubeItem?.Tempo;
        if (tempo is null)
        {
            PerformanceResultText = "Analiza o introduce el tempo antes de evaluar la batería.";
            return;
        }

        _activePerformanceLatencyCompensationMs = Math.Clamp(
            PerformanceLatencyCompensationMs + _audio.AudioInputEffectLatencyMilliseconds,
            -500d,
            500d);
        _performanceMediaKey = CurrentTrack is not null
            ? $"local:{CurrentTrack.Id}"
            : _currentYouTubeItem!.MediaKey;
        var reference = _analysisDatabase.Get(_performanceMediaKey)?.DrumReference;
        _performanceScorer.Start(
            tempo,
            _activePerformanceLatencyCompensationMs,
            reference);
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
                    _activePerformanceLatencyCompensationMs,
                    naturalEnd,
                    referenceVersion: _performanceScorer.Reference?.Version));
            SaveTrackWorkspace(silent: true);
        }
        _performanceMediaKey = null;
        PerformanceResultText = result.TotalHits == 0 && !result.UsedReference
            ? "Sesión finalizada sin golpes MIDI registrados."
            : $"Precisión {result.AccuracyPercent:0.0}% · {result.AccurateHits}/" +
              $"{(result.UsedReference ? result.ExpectedHits : result.TotalHits)} objetivos " +
              $"dentro de ±{DrumPerformanceScorer.AccurateToleranceMilliseconds:0} ms · " +
              $"{result.EarlyHits} adelantados · {result.LateHits} atrasados · " +
              (result.UsedReference
                  ? $"{result.MissedHits} omitidos · {result.ExtraHits} extras · "
                  : string.Empty) +
              $"error medio {result.MeanAbsoluteErrorMilliseconds:0.0} ms" +
              (naturalEnd ? " · canción terminada" : string.Empty);
        OnPropertyChanged(nameof(IsPerformanceEvaluationActive));
        RefreshPerformanceHistory();
    }

    private TempoAnalysisOrigin ResolveEditedTempoOrigin(string mediaKey)
    {
        var existing = _analysisDatabase.Get(mediaKey);
        return existing?.TempoOrigin is TempoAnalysisOrigin.Automatic or
            TempoAnalysisOrigin.ManuallyAdjusted or
            TempoAnalysisOrigin.OnlineSource
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

    private void RefreshDrumReferenceStatus()
    {
        if (CurrentTrack is null)
        {
            DrumReferenceStatus = _currentYouTubeItem is null
                ? "Sin pista cargada."
                : "YouTube usa la rejilla del mapa; la referencia de audio sólo se asocia a pistas locales.";
            OnPropertyChanged(nameof(CanAnalyzeDrumReference));
            return;
        }

        var reference = _analysisDatabase.Get($"local:{CurrentTrack.Id}")?.DrumReference;
        DrumReferenceStatus = reference is null
            ? "Sin referencia de batería; la puntuación usa sólo la rejilla."
            : $"{reference.HitTimesSeconds.Count} golpes de referencia · " +
              $"confianza {reference.Confidence:P0} · versión {reference.Version}.";
        OnPropertyChanged(nameof(CanAnalyzeDrumReference));
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
        RefreshDrumReferenceStatus();
        SaveTrackWorkspace();
        TempoStatus = "Datos de tempo, claqueta y puntuaciones borrados para esta pista.";
    }

    private void RecordPerformanceHit(MidiNoteMessage message)
    {
        if (!_performanceScorer.IsActive)
        {
            return;
        }

        var position = CurrentTrack is not null && _desiredTrackPlaying
            ? _audio.TrackPosition.TotalSeconds
            : _currentYouTubeItem is not null && _isYouTubeAudioActive
                ? _youtubePerformancePositionSeconds
                : -1d;
        if (position < 0d)
        {
            return;
        }
        _performanceScorer.Record(
            position,
            message.Note,
            message.Velocity);
    }

    public void UpdateYouTubePlaybackPosition(double seconds)
    {
        if (double.IsFinite(seconds) && seconds >= 0d)
        {
            _youtubePerformancePositionSeconds = seconds;
        }
    }
}
