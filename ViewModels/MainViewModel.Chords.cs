using System.Collections.ObjectModel;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using Microsoft.Win32;
using System.Net.Http;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    public event EventHandler? ChordSheetWindowRequested;
    public event EventHandler<ChordSheetLineItem?>? CurrentChordSheetLineChanged;
    public event EventHandler<ChordSheetSourceCandidate>? ChordSheetSourceOpenRequested;

    private readonly ChordSheetParser _chordSheetParser = new();
    private readonly ChordSheetAlignmentService _chordSheetAlignment = new();
    private readonly SongStructureAnalysisService _songStructureAnalysis = new();
    private readonly ChordSheetSourceSearchService _chordSheetSourceSearch = new();
    private CancellationTokenSource? _chordSheetSourceCancellation;
    private ChordSheetLineItem? _selectedChordSheetLine;
    private ChordSheetLineItem? _currentChordSheetLine;
    private string _chordSheetTextDraft = string.Empty;
    private string _chordSheetStatus =
        "Importa o pega una letra con acordes. Todo se guarda únicamente en este equipo.";
    private string _songStructureStatus = "Analiza la pista para proponer sus secciones.";
    private double _chordSheetLeadSeconds = 2d;
    private double? _chordSheetViewSwitchSeconds;
    private string? _chordSheetViewSwitchLineId;
    private string _chordSheetViewSwitchTimeText = string.Empty;
    private ChordSheetLineItem? _topChordSheetLine;
    private ChordSheetLineItem? _viewSwitchTargetLine;
    private bool _isChordSheetFollowEnabled = true;
    private bool _isSearchingChordSheetSources;
    private ChordSheetSourceCandidate? _selectedChordSheetSource;
    private string? _chordSheetSourceMediaKey;

    public ObservableCollection<ChordSheetLineItem> ChordSheetLines { get; } = [];
    public ObservableCollection<SongSection> SongSections { get; } = [];
    public ObservableCollection<ChordSheetSourceCandidate> ChordSheetSourceCandidates { get; } = [];

    public RelayCommand OpenChordSheetWindowCommand { get; private set; } = null!;
    public RelayCommand ImportChordSheetCommand { get; private set; } = null!;
    public RelayCommand SaveChordSheetTextCommand { get; private set; } = null!;
    public RelayCommand MarkChordLineNowCommand { get; private set; } = null!;
    public RelayCommand SetChordSheetViewSwitchCommand { get; private set; } = null!;
    public RelayCommand UseCurrentChordSheetViewSwitchTimeCommand { get; private set; } = null!;
    public RelayCommand ClearChordSheetViewSwitchCommand { get; private set; } = null!;
    public RelayCommand RealignChordSheetCommand { get; private set; } = null!;
    public RelayCommand ClearChordSheetCommand { get; private set; } = null!;
    public RelayCommand SearchChordSheetSourcesCommand { get; private set; } = null!;
    public RelayCommand OpenChordSheetSourceCommand { get; private set; } = null!;

    public string ChordSheetTextDraft
    {
        get => _chordSheetTextDraft;
        set => SetProperty(ref _chordSheetTextDraft, value);
    }

    public string ChordSheetStatus
    {
        get => _chordSheetStatus;
        private set => SetProperty(ref _chordSheetStatus, value);
    }

    public string SongStructureStatus
    {
        get => _songStructureStatus;
        private set => SetProperty(ref _songStructureStatus, value);
    }

    public ChordSheetLineItem? SelectedChordSheetLine
    {
        get => _selectedChordSheetLine;
        set => SetProperty(ref _selectedChordSheetLine, value);
    }

    public ChordSheetLineItem? CurrentChordSheetLine
    {
        get => _currentChordSheetLine;
        private set
        {
            if (_currentChordSheetLine == value)
            {
                return;
            }
            if (_currentChordSheetLine is not null)
            {
                _currentChordSheetLine.IsCurrent = false;
            }
            if (SetProperty(ref _currentChordSheetLine, value) && value is not null)
            {
                value.IsCurrent = true;
            }
            CurrentChordSheetLineChanged?.Invoke(this, value);
        }
    }

    public double ChordSheetLeadSeconds
    {
        get => _chordSheetLeadSeconds;
        set
        {
            if (!SetProperty(ref _chordSheetLeadSeconds, Math.Clamp(value, -10d, 20d)))
            {
                return;
            }
            SaveChordSheetFromItems();
        }
    }

    public string ChordSheetViewSwitchTimeText
    {
        get => _chordSheetViewSwitchTimeText;
        set
        {
            if (!SetProperty(ref _chordSheetViewSwitchTimeText, value ?? string.Empty))
            {
                return;
            }
            if (!ChordSheetViewportPolicy.TryParseTimestamp(value, out var seconds))
            {
                ChordSheetStatus =
                    "Tiempo no válido. Usa segundos, m:ss o h:mm:ss; por ejemplo 1:35.";
                return;
            }
            _chordSheetViewSwitchSeconds = seconds;
            OnPropertyChanged(nameof(HasChordSheetViewSwitch));
            UpdateChordSheetSwitchMarkers();
            SaveChordSheetFromItems();
            UpdateChordSheetPlaybackPosition(ResolvePlaybackPosition());
            ChordSheetStatus = _chordSheetViewSwitchLineId is null
                ? "Tiempo guardado. Selecciona la primera línea de la parte inferior y crea la marca."
                : $"Cambio de vista actualizado a {ChordSheetViewportPolicy.FormatTimestamp(seconds)}.";
        }
    }

    public bool HasChordSheetViewSwitch =>
        _chordSheetViewSwitchSeconds is not null &&
        _viewSwitchTargetLine is not null;

    public bool IsChordSheetFollowEnabled
    {
        get => _isChordSheetFollowEnabled;
        set
        {
            if (SetProperty(ref _isChordSheetFollowEnabled, value) && value)
            {
                UpdateChordSheetPlaybackPosition(ResolvePlaybackPosition());
            }
        }
    }

    public bool HasChordSheet => ChordSheetLines.Count > 0;
    public bool HasSongStructure => SongSections.Count > 0;
    public bool HasChordSheetSourceCandidates => ChordSheetSourceCandidates.Count > 0;

    public bool IsSearchingChordSheetSources
    {
        get => _isSearchingChordSheetSources;
        private set => SetProperty(ref _isSearchingChordSheetSources, value);
    }

    public ChordSheetSourceCandidate? SelectedChordSheetSource
    {
        get => _selectedChordSheetSource;
        set => SetProperty(ref _selectedChordSheetSource, value);
    }

    private void InitializeChordSheetCommands()
    {
        OpenChordSheetWindowCommand = new RelayCommand(
            () => ChordSheetWindowRequested?.Invoke(this, EventArgs.Empty));
        ImportChordSheetCommand = new RelayCommand(ImportChordSheet);
        SaveChordSheetTextCommand = new RelayCommand(SaveChordSheetText);
        MarkChordLineNowCommand = new RelayCommand(SetChordSheetViewSwitch);
        SetChordSheetViewSwitchCommand = new RelayCommand(SetChordSheetViewSwitch);
        UseCurrentChordSheetViewSwitchTimeCommand =
            new RelayCommand(UseCurrentChordSheetViewSwitchTime);
        ClearChordSheetViewSwitchCommand = new RelayCommand(ClearChordSheetViewSwitch);
        RealignChordSheetCommand = new RelayCommand(RealignCurrentChordSheet);
        ClearChordSheetCommand = new RelayCommand(ClearCurrentChordSheet);
        SearchChordSheetSourcesCommand =
            new RelayCommand(() => _ = SearchChordSheetSourcesAsync());
        OpenChordSheetSourceCommand = new RelayCommand(OpenSelectedChordSheetSource);
    }

    public async Task SearchChordSheetSourcesAsync()
    {
        var title = CurrentTrackTitle;
        if (!HasTrack || string.IsNullOrWhiteSpace(title))
        {
            ChordSheetStatus = "Carga primero una pista o vídeo para buscar su letra.";
            return;
        }
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(
            ref _chordSheetSourceCancellation,
            cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            IsSearchingChordSheetSources = true;
            ChordSheetStatus = $"Buscando páginas para «{title}»…";
            var candidates = await _chordSheetSourceSearch.SearchAsync(
                title,
                cancellation.Token);
            if (!string.Equals(CurrentTrackTitle, title, StringComparison.Ordinal))
            {
                return;
            }
            ChordSheetSourceCandidates.Clear();
            foreach (var candidate in candidates)
            {
                ChordSheetSourceCandidates.Add(candidate);
            }
            SelectedChordSheetSource = ChordSheetSourceCandidates.FirstOrDefault();
            _chordSheetSourceMediaKey = CurrentAnalysisMediaKey;
            OnPropertyChanged(nameof(HasChordSheetSourceCandidates));
            ChordSheetStatus = candidates.Count == 0
                ? "No se encontraron opciones claras. Usa el navegador para buscar manualmente."
                : $"{candidates.Count} páginas candidatas. Abre y comprueba la versión antes de extraer.";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ChordSheetStatus = "Búsqueda cancelada.";
        }
        catch (Exception exception) when (exception is
            HttpRequestException or
            IOException or
            TaskCanceledException)
        {
            ChordSheetStatus =
                $"No se pudo completar la búsqueda: {exception.Message}. Puedes buscar manualmente.";
        }
        finally
        {
            IsSearchingChordSheetSources = false;
            Interlocked.CompareExchange(
                ref _chordSheetSourceCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private void OpenSelectedChordSheetSource()
    {
        if (SelectedChordSheetSource is null)
        {
            ChordSheetStatus = "Selecciona primero una página candidata.";
            return;
        }
        ChordSheetSourceOpenRequested?.Invoke(this, SelectedChordSheetSource);
    }

    public void SetChordSheetFromWeb(string text, string? sourceUrl, string? title)
    {
        if (string.IsNullOrWhiteSpace(CurrentAnalysisMediaKey))
        {
            ChordSheetStatus = "Carga primero la pista o el vídeo al que pertenece esta letra.";
            return;
        }
        var normalized = ChordSheetParser.NormalizeExtractedText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            ChordSheetStatus =
                "No se encontró texto. Selecciona con el ratón la letra y los acordes y vuelve a extraer.";
            return;
        }
        ChordSheetTextDraft = normalized;
        SaveChordSheet(
            _chordSheetParser.Parse(
                normalized,
                string.IsNullOrWhiteSpace(title) ? CurrentTrackTitle : title,
                ChordSheetSourceKind.WebSelection,
                sourceUrl),
            realign: false);
        ChordSheetStatus =
            "Texto web guardado. Selecciona dónde debe empezar la parte inferior y crea una única marca.";
    }

    private void ImportChordSheet()
    {
        if (CurrentAnalysisMediaKey is null)
        {
            ChordSheetStatus = "Carga primero una pista o vídeo.";
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = "Importar letra y acordes",
            Filter = "Texto y ChordPro|*.txt;*.cho;*.chopro;*.pro;*.crd|Todos los archivos|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        try
        {
            var text = File.ReadAllText(dialog.FileName);
            ChordSheetTextDraft = text;
            SaveChordSheet(
                _chordSheetParser.Parse(
                    text,
                    Path.GetFileNameWithoutExtension(dialog.FileName),
                    ChordSheetSourceKind.ImportedFile),
                realign: false);
            ChordSheetStatus = $"Importado y procesado: {Path.GetFileName(dialog.FileName)}";
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            ChordSheetStatus = $"No se pudo importar: {exception.Message}";
        }
    }

    private void SaveChordSheetText()
    {
        if (CurrentAnalysisMediaKey is null)
        {
            ChordSheetStatus = "Carga primero una pista o vídeo.";
            return;
        }
        if (string.IsNullOrWhiteSpace(ChordSheetTextDraft))
        {
            ChordSheetStatus = "Pega o escribe primero la letra y los acordes.";
            return;
        }
        SaveChordSheet(
            _chordSheetParser.Parse(
                ChordSheetTextDraft,
                CurrentTrackTitle,
                ChordSheetSourceKind.UserText),
            realign: false);
        ChordSheetStatus =
            "Hoja guardada localmente. Configura una sola marca si necesitas mostrar la parte inferior.";
    }

    private void SaveChordSheet(ChordSheetDocument document, bool realign)
    {
        var mediaKey = CurrentAnalysisMediaKey;
        if (mediaKey is null)
        {
            return;
        }
        var structure = _analysisDatabase.Get(mediaKey)?.SongStructure;
        if (realign)
        {
            document = _chordSheetAlignment.Align(
                document,
                structure,
                ResolveCurrentMediaDuration(structure));
        }
        document = ChordSheetDocument.Normalize(document with
        {
            LeadSeconds = ChordSheetLeadSeconds
        });
        _analysisDatabase.SetChordSheet(mediaKey, document);
        LoadChordSheet(document);
        SaveTrackWorkspace();
    }

    private void SaveChordSheetFromItems()
    {
        var mediaKey = CurrentAnalysisMediaKey;
        var existing = _analysisDatabase.Get(mediaKey)?.ChordSheet;
        if (mediaKey is null || existing is null)
        {
            return;
        }
        var updated = ChordSheetDocument.Normalize(existing with
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            LeadSeconds = ChordSheetLeadSeconds,
            Lines = ChordSheetLines.Select(item => item.ToModel()).ToArray(),
            ViewSwitchSeconds = _chordSheetViewSwitchSeconds,
            ViewSwitchLineId = _chordSheetViewSwitchLineId
        });
        _analysisDatabase.SetChordSheet(mediaKey, updated);
        ScheduleSettingsSave();
    }

    private void SetChordSheetViewSwitch()
    {
        var line = SelectedChordSheetLine;
        if (line is null)
        {
            ChordSheetStatus =
                "Selecciona la primera línea que quieres ver después del cambio.";
            return;
        }
        double seconds;
        if (string.IsNullOrWhiteSpace(_chordSheetViewSwitchTimeText))
        {
            seconds = ResolvePlaybackPosition();
        }
        else if (!ChordSheetViewportPolicy.TryParseTimestamp(
                     _chordSheetViewSwitchTimeText,
                     out seconds))
        {
            ChordSheetStatus =
                "Tiempo no válido. Usa segundos, m:ss o h:mm:ss; por ejemplo 1:35.";
            return;
        }
        _chordSheetViewSwitchSeconds = seconds;
        _chordSheetViewSwitchLineId = line.Id;
        _chordSheetViewSwitchTimeText = ChordSheetViewportPolicy.FormatTimestamp(seconds);
        OnPropertyChanged(nameof(ChordSheetViewSwitchTimeText));
        UpdateChordSheetSwitchMarkers();
        SaveChordSheetFromItems();
        ChordSheetStatus =
            $"Cambio único fijado en {_chordSheetViewSwitchTimeText}; mostrará «{line.Text}» y lo que sigue.";
        UpdateChordSheetPlaybackPosition(seconds);
    }

    private void UseCurrentChordSheetViewSwitchTime()
    {
        var seconds = ResolvePlaybackPosition();
        _chordSheetViewSwitchSeconds = seconds;
        _chordSheetViewSwitchTimeText = ChordSheetViewportPolicy.FormatTimestamp(seconds);
        OnPropertyChanged(nameof(ChordSheetViewSwitchTimeText));
        UpdateChordSheetSwitchMarkers();
        SaveChordSheetFromItems();
        ChordSheetStatus =
            $"Tiempo preparado en {_chordSheetViewSwitchTimeText}. Selecciona la línea inferior y aplícalo.";
    }

    private void ClearChordSheetViewSwitch()
    {
        _chordSheetViewSwitchSeconds = null;
        _chordSheetViewSwitchLineId = null;
        _chordSheetViewSwitchTimeText = string.Empty;
        CurrentChordSheetLine = null;
        UpdateChordSheetSwitchMarkers();
        OnPropertyChanged(nameof(ChordSheetViewSwitchTimeText));
        SaveChordSheetFromItems();
        ChordSheetStatus =
            "Cambio automático eliminado. La hoja queda disponible para desplazamiento manual.";
    }

    private void RealignCurrentChordSheet()
    {
        var mediaKey = CurrentAnalysisMediaKey;
        var record = _analysisDatabase.Get(mediaKey);
        if (mediaKey is null || record?.ChordSheet is null)
        {
            ChordSheetStatus = "No hay una hoja que alinear.";
            return;
        }
        var aligned = _chordSheetAlignment.Align(
            record.ChordSheet,
            record.SongStructure,
            ResolveCurrentMediaDuration(record.SongStructure));
        _analysisDatabase.SetChordSheet(mediaKey, aligned);
        LoadChordSheet(aligned);
        SaveTrackWorkspace();
        ChordSheetStatus =
            "Alineación recalculada. Las marcas manuales existentes se han conservado.";
    }

    private void ClearCurrentChordSheet()
    {
        var mediaKey = CurrentAnalysisMediaKey;
        if (mediaKey is null)
        {
            return;
        }
        _analysisDatabase.SetChordSheet(mediaKey, null);
        LoadChordSheet(null);
        SaveTrackWorkspace();
        ChordSheetStatus = "Hoja desvinculada; no se ha borrado ningún archivo de audio.";
    }

    private void LoadChordSheetForCurrentMedia()
    {
        if (!string.Equals(
                _chordSheetSourceMediaKey,
                CurrentAnalysisMediaKey,
                StringComparison.Ordinal))
        {
            ChordSheetSourceCandidates.Clear();
            SelectedChordSheetSource = null;
            _chordSheetSourceMediaKey = null;
            OnPropertyChanged(nameof(HasChordSheetSourceCandidates));
        }
        var record = _analysisDatabase.Get(CurrentAnalysisMediaKey);
        LoadChordSheet(record?.ChordSheet);
        LoadSongStructure(record?.SongStructure);
    }

    private void LoadChordSheet(ChordSheetDocument? document)
    {
        ChordSheetLines.Clear();
        CurrentChordSheetLine = null;
        if (document is not null)
        {
            document = ChordSheetDocument.Normalize(document);
            foreach (var line in document.Lines)
            {
                ChordSheetLines.Add(new ChordSheetLineItem(line));
            }
            _chordSheetTextDraft = document.RawText;
            _chordSheetLeadSeconds = document.LeadSeconds;
            _chordSheetViewSwitchSeconds = document.ViewSwitchSeconds;
            _chordSheetViewSwitchLineId = document.ViewSwitchLineId;
            _chordSheetViewSwitchTimeText =
                ChordSheetViewportPolicy.FormatTimestamp(document.ViewSwitchSeconds);
            UpdateChordSheetSwitchMarkers();
            ChordSheetStatus = HasChordSheetViewSwitch
                ? $"{ChordSheetLines.Count} líneas · un cambio automático configurado."
                : $"{ChordSheetLines.Count} líneas · desplazamiento manual; puedes crear una marca única.";
        }
        else
        {
            _chordSheetTextDraft = string.Empty;
            _chordSheetLeadSeconds = 2d;
            _chordSheetViewSwitchSeconds = null;
            _chordSheetViewSwitchLineId = null;
            _chordSheetViewSwitchTimeText = string.Empty;
            _topChordSheetLine = null;
            _viewSwitchTargetLine = null;
            ChordSheetStatus =
                "Importa o pega una letra con acordes. Todo se guarda únicamente en este equipo.";
        }
        OnPropertyChanged(nameof(ChordSheetTextDraft));
        OnPropertyChanged(nameof(ChordSheetLeadSeconds));
        OnPropertyChanged(nameof(ChordSheetViewSwitchTimeText));
        OnPropertyChanged(nameof(HasChordSheetViewSwitch));
        OnPropertyChanged(nameof(HasChordSheet));
    }

    private void LoadSongStructure(SongStructureMap? structure)
    {
        SongSections.Clear();
        if (structure is not null)
        {
            structure = SongStructureMap.Normalize(structure);
            foreach (var section in structure.Sections)
            {
                SongSections.Add(section);
            }
            SongStructureStatus =
                $"{SongSections.Count} secciones propuestas · confianza {structure.Confidence:P0}.";
        }
        else
        {
            SongStructureStatus = "Analiza la pista para proponer sus secciones.";
        }
        OnPropertyChanged(nameof(HasSongStructure));
    }

    private void ApplySongStructureAnalysis(string mediaKey, SongStructureMap structure)
    {
        _analysisDatabase.SetSongStructure(mediaKey, structure);
        if (string.Equals(mediaKey, CurrentAnalysisMediaKey, StringComparison.Ordinal))
        {
            LoadSongStructure(structure);
        }
        SaveTrackWorkspace(silent: true);
    }

    private void UpdateChordSheetSwitchMarkers()
    {
        _topChordSheetLine = ChordSheetLines.FirstOrDefault(line =>
                                 line.Kind != ChordSheetLineKind.Empty)
                             ?? ChordSheetLines.FirstOrDefault();
        _viewSwitchTargetLine = ChordSheetLines.FirstOrDefault(line =>
            string.Equals(line.Id, _chordSheetViewSwitchLineId, StringComparison.Ordinal));
        foreach (var line in ChordSheetLines)
        {
            line.SetViewSwitchTarget(
                line == _viewSwitchTargetLine && _chordSheetViewSwitchSeconds is not null,
                _chordSheetViewSwitchSeconds);
        }
        OnPropertyChanged(nameof(HasChordSheetViewSwitch));
    }

    private void UpdateChordSheetPlaybackPosition(double seconds)
    {
        if (!IsChordSheetFollowEnabled)
        {
            return;
        }
        var anchorId = ChordSheetViewportPolicy.ResolveAnchorLineId(
            ChordSheetLines.Select(line => line.ToModel()).ToArray(),
            seconds,
            _chordSheetViewSwitchSeconds,
            _chordSheetViewSwitchLineId);
        if (anchorId is null)
        {
            return;
        }
        CurrentChordSheetLine = ChordSheetLines.FirstOrDefault(line =>
            string.Equals(line.Id, anchorId, StringComparison.Ordinal));
    }

    private double ResolvePlaybackPosition() => CurrentTrack is not null
        ? _audio.TrackPosition.TotalSeconds
        : Math.Max(0d, _youtubePerformancePositionSeconds);

    private double ResolveCurrentMediaDuration(SongStructureMap? structure) =>
        CurrentTrack is not null
            ? Math.Max(_audio.TrackDuration.TotalSeconds, structure?.DurationSeconds ?? 0d)
            : structure?.DurationSeconds ?? 0d;
}
