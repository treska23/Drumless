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
    private ChordSheetViewportMarkerItem? _selectedChordSheetViewportMarker;
    private string _chordSheetTextDraft = string.Empty;
    private string _chordSheetStatus =
        "Importa o pega una letra con acordes. Todo se guarda únicamente en este equipo.";
    private string _songStructureStatus = "Analiza la pista para proponer sus secciones.";
    private double _chordSheetLeadSeconds = 2d;
    private string _chordSheetViewSwitchTimeText = string.Empty;
    private bool _isChordSheetFollowEnabled = true;
    private bool _isSearchingChordSheetSources;
    private ChordSheetSourceCandidate? _selectedChordSheetSource;
    private string? _chordSheetSourceMediaKey;

    public ObservableCollection<ChordSheetLineItem> ChordSheetLines { get; } = [];
    public ObservableCollection<ChordSheetViewportMarkerItem> ChordSheetViewportMarkers { get; } = [];
    public ObservableCollection<SongSection> SongSections { get; } = [];
    public ObservableCollection<ChordSheetSourceCandidate> ChordSheetSourceCandidates { get; } = [];

    public RelayCommand OpenChordSheetWindowCommand { get; private set; } = null!;
    public RelayCommand ImportChordSheetCommand { get; private set; } = null!;
    public RelayCommand SaveChordSheetTextCommand { get; private set; } = null!;
    public RelayCommand MarkChordLineNowCommand { get; private set; } = null!;
    public RelayCommand SetChordSheetViewSwitchCommand { get; private set; } = null!;
    public RelayCommand UseCurrentChordSheetViewSwitchTimeCommand { get; private set; } = null!;
    public RelayCommand MoveSelectedChordSheetViewportMarkerCommand { get; private set; } = null!;
    public RelayCommand ClearChordSheetViewSwitchCommand { get; private set; } = null!;
    public RelayCommand ClearAllChordSheetViewSwitchesCommand { get; private set; } = null!;
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
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }
            if (!ChordSheetViewportPolicy.TryParseTimestamp(value, out var seconds))
            {
                ChordSheetStatus =
                    "Tiempo no válido. Usa segundos, m:ss o h:mm:ss; por ejemplo 1:35.";
                return;
            }
            ChordSheetStatus =
                $"Tiempo preparado en {ChordSheetViewportPolicy.FormatTimestamp(seconds)}. Selecciona la línea de destino y añade la marca.";
        }
    }

    public ChordSheetViewportMarkerItem? SelectedChordSheetViewportMarker
    {
        get => _selectedChordSheetViewportMarker;
        set
        {
            if (!SetProperty(ref _selectedChordSheetViewportMarker, value) || value is null)
            {
                return;
            }
            _chordSheetViewSwitchTimeText = value.TimeLabel;
            OnPropertyChanged(nameof(ChordSheetViewSwitchTimeText));
        }
    }

    public bool HasChordSheetViewSwitch => ChordSheetViewportMarkers.Count > 0;

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
        MoveSelectedChordSheetViewportMarkerCommand =
            new RelayCommand(MoveSelectedChordSheetViewportMarker);
        ClearChordSheetViewSwitchCommand = new RelayCommand(ClearChordSheetViewSwitch);
        ClearAllChordSheetViewSwitchesCommand =
            new RelayCommand(ClearAllChordSheetViewSwitches);
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
            "Texto web guardado. Añade una marca para cada cambio de bloque que necesites.";
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
            "Hoja guardada localmente. Puedes configurar tantas marcas de cambio como necesites.";
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
            ViewportMarkers = ChordSheetViewportMarkers
                .Select(marker => marker.ToModel())
                .ToArray()
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
        var conflicts = ChordSheetViewportMarkers
            .Where(marker => Math.Abs(marker.Seconds - seconds) < 0.001d)
            .ToArray();
        foreach (var conflict in conflicts)
        {
            ChordSheetViewportMarkers.Remove(conflict);
        }
        var marker = new ChordSheetViewportMarkerItem(
            new ChordSheetViewportMarker(
                conflicts.FirstOrDefault()?.Id ?? Guid.NewGuid().ToString("N"),
                seconds,
                line.Id),
            line.Text);
        ChordSheetViewportMarkers.Add(marker);
        SortChordSheetViewportMarkers();
        SelectedChordSheetViewportMarker = marker;
        _chordSheetViewSwitchTimeText = ChordSheetViewportPolicy.FormatTimestamp(seconds);
        OnPropertyChanged(nameof(ChordSheetViewSwitchTimeText));
        UpdateChordSheetSwitchMarkers();
        SaveChordSheetFromItems();
        ChordSheetStatus =
            $"Marca añadida en {_chordSheetViewSwitchTimeText}; mostrará «{line.Text}» y lo que sigue. Hay {ChordSheetViewportMarkers.Count}.";
        UpdateChordSheetPlaybackPosition(seconds);
    }

    private void UseCurrentChordSheetViewSwitchTime()
    {
        var seconds = ResolvePlaybackPosition();
        _chordSheetViewSwitchTimeText = ChordSheetViewportPolicy.FormatTimestamp(seconds);
        OnPropertyChanged(nameof(ChordSheetViewSwitchTimeText));
        ChordSheetStatus =
            $"Tiempo preparado en {_chordSheetViewSwitchTimeText}. Selecciona la línea que debe aparecer y añade la marca.";
    }

    private void MoveSelectedChordSheetViewportMarker()
    {
        var marker = SelectedChordSheetViewportMarker;
        if (marker is null)
        {
            ChordSheetStatus = "Selecciona primero la marca que quieres mover.";
            return;
        }
        var line = SelectedChordSheetLine;
        if (line is null)
        {
            ChordSheetStatus = "Selecciona la nueva línea de destino.";
            return;
        }

        var seconds = marker.Seconds;
        if (!string.IsNullOrWhiteSpace(_chordSheetViewSwitchTimeText) &&
            !ChordSheetViewportPolicy.TryParseTimestamp(
                _chordSheetViewSwitchTimeText,
                out seconds))
        {
            ChordSheetStatus =
                "Tiempo no válido. Déjalo vacío para conservarlo o usa segundos, m:ss o h:mm:ss.";
            return;
        }
        var timeConflict = ChordSheetViewportMarkers.FirstOrDefault(candidate =>
            candidate != marker && Math.Abs(candidate.Seconds - seconds) < 0.001d);
        if (timeConflict is not null)
        {
            ChordSheetStatus =
                $"Ya existe otra marca en {ChordSheetViewportPolicy.FormatTimestamp(seconds)}. Usa un tiempo distinto.";
            return;
        }

        ChordSheetViewportMarkers.Remove(marker);
        var moved = new ChordSheetViewportMarkerItem(
            new ChordSheetViewportMarker(marker.Id, seconds, line.Id),
            line.Text);
        ChordSheetViewportMarkers.Add(moved);
        SortChordSheetViewportMarkers();
        SelectedChordSheetViewportMarker = moved;
        _chordSheetViewSwitchTimeText = ChordSheetViewportPolicy.FormatTimestamp(seconds);
        OnPropertyChanged(nameof(ChordSheetViewSwitchTimeText));
        UpdateChordSheetSwitchMarkers();
        SaveChordSheetFromItems();
        UpdateChordSheetPlaybackPosition(ResolvePlaybackPosition());
        ChordSheetStatus =
            $"Marca de {_chordSheetViewSwitchTimeText} movida a «{line.Text}».";
    }

    private void ClearChordSheetViewSwitch()
    {
        var marker = SelectedChordSheetViewportMarker;
        if (marker is null)
        {
            ChordSheetStatus =
                "Selecciona una marca de la lista para quitarla.";
            return;
        }
        ChordSheetViewportMarkers.Remove(marker);
        SelectedChordSheetViewportMarker = null;
        UpdateChordSheetSwitchMarkers();
        SaveChordSheetFromItems();
        ChordSheetStatus =
            $"Marca de {ChordSheetViewportPolicy.FormatTimestamp(marker.Seconds)} eliminada. Quedan {ChordSheetViewportMarkers.Count}.";
        UpdateChordSheetPlaybackPosition(ResolvePlaybackPosition());
    }

    private void ClearAllChordSheetViewSwitches()
    {
        if (ChordSheetViewportMarkers.Count == 0)
        {
            ChordSheetStatus = "No hay marcas que quitar.";
            return;
        }
        ChordSheetViewportMarkers.Clear();
        SelectedChordSheetViewportMarker = null;
        CurrentChordSheetLine = null;
        UpdateChordSheetSwitchMarkers();
        SaveChordSheetFromItems();
        ChordSheetStatus =
            "Todas las marcas automáticas se han eliminado; el texto permanece intacto.";
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
            ChordSheetViewportMarkers.Clear();
            foreach (var marker in document.ViewportMarkers ?? [])
            {
                var lineText = document.Lines.FirstOrDefault(line =>
                    string.Equals(line.Id, marker.LineId, StringComparison.Ordinal))?.Text;
                ChordSheetViewportMarkers.Add(new ChordSheetViewportMarkerItem(
                    marker,
                    lineText ?? string.Empty));
            }
            SelectedChordSheetViewportMarker = null;
            _chordSheetViewSwitchTimeText = string.Empty;
            UpdateChordSheetSwitchMarkers();
            ChordSheetStatus = HasChordSheetViewSwitch
                ? $"{ChordSheetLines.Count} líneas · {ChordSheetViewportMarkers.Count} marcas automáticas configuradas."
                : $"{ChordSheetLines.Count} líneas · desplazamiento manual; puedes crear todas las marcas que necesites.";
        }
        else
        {
            _chordSheetTextDraft = string.Empty;
            _chordSheetLeadSeconds = 2d;
            ChordSheetViewportMarkers.Clear();
            SelectedChordSheetViewportMarker = null;
            _chordSheetViewSwitchTimeText = string.Empty;
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
        foreach (var line in ChordSheetLines)
        {
            var markerTimes = ChordSheetViewportMarkers
                .Where(candidate =>
                    string.Equals(candidate.LineId, line.Id, StringComparison.Ordinal))
                .Select(candidate => candidate.Seconds)
                .OrderBy(seconds => seconds)
                .ToArray();
            line.SetViewSwitchTargets(markerTimes);
        }
        OnPropertyChanged(nameof(HasChordSheetViewSwitch));
    }

    private void SortChordSheetViewportMarkers()
    {
        var ordered = ChordSheetViewportMarkers
            .OrderBy(marker => marker.Seconds)
            .ToArray();
        ChordSheetViewportMarkers.Clear();
        foreach (var marker in ordered)
        {
            ChordSheetViewportMarkers.Add(marker);
        }
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
            ChordSheetViewportMarkers.Select(marker => marker.ToModel()).ToArray());
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
