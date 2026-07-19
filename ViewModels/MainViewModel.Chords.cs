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

    public bool IsChordSheetFollowEnabled
    {
        get => _isChordSheetFollowEnabled;
        set => SetProperty(ref _isChordSheetFollowEnabled, value);
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
        MarkChordLineNowCommand = new RelayCommand(MarkSelectedChordLineNow);
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
            realign: true);
        ChordSheetStatus =
            "Texto web guardado localmente y alineado de forma preliminar. Revisa las marcas amarillas.";
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
                realign: true);
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
            realign: true);
        ChordSheetStatus =
            "Hoja guardada localmente. La alineación automática es una propuesta editable.";
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
            Lines = ChordSheetLines.Select(item => item.ToModel()).ToArray()
        });
        _analysisDatabase.SetChordSheet(mediaKey, updated);
        ScheduleSettingsSave();
    }

    private void MarkSelectedChordLineNow()
    {
        var line = SelectedChordSheetLine;
        if (line is null)
        {
            ChordSheetStatus = "Selecciona la línea que empieza en la posición actual.";
            return;
        }
        var seconds = ResolvePlaybackPosition();
        line.SetManualStart(seconds);
        var index = ChordSheetLines.IndexOf(line);
        var next = ChordSheetLines
            .Skip(index + 1)
            .FirstOrDefault(item => item.StartSeconds is { } start && start > seconds);
        var unsynchronized = ChordSheetLines
            .Skip(index + 1)
            .TakeWhile(item => item != next)
            .Where(item => item.Kind != ChordSheetLineKind.Empty)
            .ToArray();
        if (next?.StartSeconds is { } end && unsynchronized.Length > 0)
        {
            for (var offset = 0; offset < unsynchronized.Length; offset++)
            {
                unsynchronized[offset].StartSeconds =
                    seconds + ((end - seconds) * (offset + 1d) / (unsynchronized.Length + 1d));
            }
        }
        SaveChordSheetFromItems();
        ChordSheetStatus = $"Marca fijada en {TimeSpan.FromSeconds(seconds):mm\\:ss\\.f}.";
        UpdateChordSheetPlaybackPosition(seconds);
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
            ChordSheetStatus = $"{ChordSheetLines.Count} líneas · seguimiento preparado.";
        }
        else
        {
            _chordSheetTextDraft = string.Empty;
            _chordSheetLeadSeconds = 2d;
            ChordSheetStatus =
                "Importa o pega una letra con acordes. Todo se guarda únicamente en este equipo.";
        }
        OnPropertyChanged(nameof(ChordSheetTextDraft));
        OnPropertyChanged(nameof(ChordSheetLeadSeconds));
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
            var chordSheet = _analysisDatabase.Get(mediaKey)?.ChordSheet;
            if (chordSheet is not null)
            {
                var aligned = _chordSheetAlignment.Align(
                    chordSheet,
                    structure,
                    structure.DurationSeconds);
                _analysisDatabase.SetChordSheet(mediaKey, aligned);
                LoadChordSheet(aligned);
            }
        }
        SaveTrackWorkspace(silent: true);
    }

    private void UpdateChordSheetPlaybackPosition(double seconds)
    {
        if (!IsChordSheetFollowEnabled || ChordSheetLines.Count == 0)
        {
            return;
        }
        var target = Math.Max(0d, seconds + ChordSheetLeadSeconds);
        ChordSheetLineItem? selected = null;
        foreach (var line in ChordSheetLines)
        {
            if (line.StartSeconds is not { } start || start > target)
            {
                continue;
            }
            selected = line;
        }
        CurrentChordSheetLine = selected;
    }

    private double ResolvePlaybackPosition() => CurrentTrack is not null
        ? _audio.TrackPosition.TotalSeconds
        : Math.Max(0d, _youtubePerformancePositionSeconds);

    private double ResolveCurrentMediaDuration(SongStructureMap? structure) =>
        CurrentTrack is not null
            ? Math.Max(_audio.TrackDuration.TotalSeconds, structure?.DurationSeconds ?? 0d)
            : structure?.DurationSeconds ?? 0d;
}
