using System.Collections.ObjectModel;
using System.ComponentModel;
using DrumPracticeStudio.Audio;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using Microsoft.Win32;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    public event EventHandler<YouTubePlaybackRequest>? YouTubePlaybackRequested;
    public event EventHandler<YouTubeControlRequest>? YouTubeControlRequested;
    private readonly StudioStateStore _studioStateStore = new();
    private readonly TrackLibraryService _trackLibrary = new();
    private readonly MediaAnalysisDatabase _analysisDatabase = new();
    private readonly PlaybackNavigator _playbackNavigator = new();
    private readonly Dictionary<string, PlaylistItem> _playlistPlaybackItems = new(StringComparer.Ordinal);
    private CancellationTokenSource? _trackLoadCancellation;
    private long _trackLoadSequence;
    private long _activeLoadGeneration;
    private long _activeRunGeneration;
    private bool _desiredTrackPlaying;
    private bool _isTrackLoading;
    private bool _isInitializingTrackWorkspace;
    private bool _isUpdatingPlaylistMix;
    private string? _trackWorkspaceWarning;

    private string _outputFolderPath = AppPaths.DerivedTracks;
    private LocalTrack? _selectedLibraryTrack;
    private Playlist? _selectedPlaylist;
    private PlaylistItemViewModel? _selectedPlaylistItem;
    private PlaylistItem? _currentYouTubeItem;
    private bool _playlistQueueActive;
    private PlaybackModeOption? _selectedPlaybackMode;
    private string _playlistNameDraft = string.Empty;

    public ObservableCollection<Playlist> Playlists { get; }
    public ObservableCollection<PlaylistItemViewModel> PlaylistItems { get; }
    public ObservableCollection<PlaybackModeOption> PlaybackModeOptions { get; }

    public RelayCommand<LocalTrack> LoadTrackCommand { get; private set; } = null!;
    public RelayCommand<LocalTrack> RemoveLibraryTrackCommand { get; private set; } = null!;
    public RelayCommand ChooseOutputFolderCommand { get; private set; } = null!;
    public RelayCommand RescanLibraryCommand { get; private set; } = null!;
    public RelayCommand CreatePlaylistCommand { get; private set; } = null!;
    public RelayCommand RenamePlaylistCommand { get; private set; } = null!;
    public RelayCommand DeletePlaylistCommand { get; private set; } = null!;
    public RelayCommand ClearPlaylistMixCommand { get; private set; } = null!;
    public RelayCommand PlayPlaylistQueueCommand { get; private set; } = null!;
    public RelayCommand<LocalTrack> AddTrackToPlaylistCommand { get; private set; } = null!;
    public RelayCommand<PlaylistItemViewModel> PlayPlaylistItemCommand { get; private set; } = null!;
    public RelayCommand<PlaylistItemViewModel> RemovePlaylistItemCommand { get; private set; } = null!;
    public RelayCommand<PlaylistItemViewModel> MovePlaylistItemUpCommand { get; private set; } = null!;
    public RelayCommand<PlaylistItemViewModel> MovePlaylistItemDownCommand { get; private set; } = null!;
    public RelayCommand PreviousTrackCommand { get; private set; } = null!;
    public RelayCommand NextTrackCommand { get; private set; } = null!;

    public string OutputFolderPath
    {
        get => _outputFolderPath;
        private set => SetProperty(ref _outputFolderPath, value);
    }

    public LocalTrack? SelectedLibraryTrack
    {
        get => _selectedLibraryTrack;
        set => SetProperty(ref _selectedLibraryTrack, value);
    }

    public Playlist? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set
        {
            if (!SetProperty(ref _selectedPlaylist, value))
            {
                return;
            }

            PlaylistNameDraft = value?.Name ?? string.Empty;
            RebuildPlaylistItems();
            ResetPlaylistPlaybackQueue(_playbackNavigator.CurrentTrackId);
            OnPropertyChanged(nameof(MixedPlaylistSummary));
            OnPropertyChanged(nameof(SelectedPlaylistSummary));
            if (!_isInitializingTrackWorkspace)
            {
                SaveTrackWorkspace();
            }
        }
    }

    public PlaylistItemViewModel? SelectedPlaylistItem
    {
        get => _selectedPlaylistItem;
        set => SetProperty(ref _selectedPlaylistItem, value);
    }

    public PlaybackModeOption? SelectedPlaybackMode
    {
        get => _selectedPlaybackMode;
        set
        {
            if (!SetProperty(ref _selectedPlaybackMode, value) || value is null)
            {
                return;
            }

            _playbackNavigator.Mode = value.Mode;
            if (!_isInitializingTrackWorkspace)
            {
                SaveTrackWorkspace();
            }
        }
    }

    public string PlaylistNameDraft
    {
        get => _playlistNameDraft;
        set => SetProperty(ref _playlistNameDraft, value);
    }

    public string LibrarySummary
    {
        get
        {
            var missing = Tracks.Count(track => track.IsMissing);
            var total = Tracks.Count == 1 ? "1 pista" : $"{Tracks.Count} pistas";
            return missing == 0 ? total : $"{total} · {missing} sin archivo";
        }
    }

    public string MixedPlaylistSummary
    {
        get
        {
            var included = Playlists.Where(playlist => playlist.IsIncludedInMix).ToArray();
            if (included.Length == 0)
            {
                return SelectedPlaylist is null
                    ? "Sin mezcla · se reproduce la biblioteca completa"
                    : $"Sin mezcla · se reproduce {SelectedPlaylist.Name}";
            }

            var trackCount = PlaylistMixService.BuildQueue(Playlists, SelectedPlaylist)
                .Count(IsPlaylistItemAvailable);
            var playlistLabel = included.Length == 1
                ? included[0].Name
                : $"{included.Length} playlists";
            var trackLabel = trackCount == 1 ? "1 pista" : $"{trackCount} pistas";
            return $"{playlistLabel} · {trackLabel} en la cola";
        }
    }

    public string SelectedPlaylistSummary
    {
        get
        {
            if (SelectedPlaylist is null)
            {
                return "Selecciona o crea una playlist";
            }

            var missing = SelectedPlaylist.Items.Count(item => !IsPlaylistItemAvailable(item));
            var total = SelectedPlaylist.Items.Count;
            var youtube = SelectedPlaylist.Items.Count(item => item.Kind == PlaylistItemKind.YouTube);
            var totalLabel = total == 1 ? "1 elemento" : $"{total} elementos";
            if (youtube > 0)
            {
                totalLabel += $" · {youtube} YouTube";
            }
            return missing == 0 ? totalLabel : $"{totalLabel} · {missing} sin archivo";
        }
    }

    private void InitializeTrackWorkspace()
    {
        _isInitializingTrackWorkspace = true;
        try
        {
            var state = _studioStateStore.Load();
            _trackWorkspaceWarning = _studioStateStore.LastLoadWarning;
            _preferredAudioOutputDeviceId = state.AudioOutputDeviceId;
            _preferredAudioInputOutputDeviceId = state.AudioInputOutputDeviceId;
            _preferredAudioInputChannelIndex = state.AudioInputChannelIndex;
            _audioInputGain = Math.Clamp(state.AudioInputGain, 0d, 1.5d);
            _preferredAudioInputMonitors.Clear();
            _preferredAudioInputMonitors.AddRange(state.AudioInputMonitors);
            foreach (var bus in AudioEffectBuses)
            {
                var saved = state.AudioEffectBuses.FirstOrDefault(
                    setting => setting.Target == bus.Target);
                bus.Load(saved ?? new AudioEffectBusSetting(bus.Target));
                var setting = bus.ToSetting();
                _audio.ConfigureEffectBus(
                    setting.Target,
                    setting.EffectiveEffects,
                    setting.EffectsBypassed);
            }
            _preferredMidiDeviceName = state.MidiDeviceName;
            _preferredMidiDeviceIndex = state.MidiDeviceIndex;
            _autoConnectMidi = state.AutoConnectMidi;
            _midiVelocitySensitivity = Math.Clamp(state.MidiVelocitySensitivity, 0d, 100d);
            _preferredInternalLibraryId = state.ActiveLibraryId;
            _preferredInternalKitId = state.ActiveKitId;
            _trackVolume = Math.Clamp(state.TrackVolume, 0d, 1d);
            _preferredVstModulePath = state.VstModulePath;
            _preferredVstClassId = state.VstClassId;
            _autoLoadVst = state.AutoLoadVst;
            _keepDrums = state.StemSelection.HasFlag(StemSelection.Drums);
            _keepBass = state.StemSelection.HasFlag(StemSelection.Bass);
            _keepVocals = state.StemSelection.HasFlag(StemSelection.Vocals);
            _keepGuitar = state.StemSelection.HasFlag(StemSelection.Guitar);
            _keepPiano = state.StemSelection.HasFlag(StemSelection.Piano);
            _keepOther = state.StemSelection.HasFlag(StemSelection.Other);
            _performanceLatencyCompensationMs = state.PerformanceLatencyCompensationMs;
            _analysisDatabase.Load(state.AnalysisRecords);
            _trackLibrary.Load(state.Tracks);
            foreach (var track in Tracks)
            {
                _analysisDatabase.ImportTempoIfMissing($"local:{track.Id}", track.Tempo);
                track.Tempo = _analysisDatabase.GetTempo($"local:{track.Id}");
            }
            _lastRecordingTrack = Tracks
                .Where(track => track.Variant == TrackVariant.Recording && track.IsAvailable)
                .OrderByDescending(track => File.GetLastWriteTimeUtc(track.Path))
                .FirstOrDefault();

            foreach (var playlist in state.Playlists)
            {
                foreach (var item in playlist.Items)
                {
                    _analysisDatabase.ImportTempoIfMissing(item.MediaKey, item.Tempo);
                    item.Tempo = _analysisDatabase.GetTempo(item.MediaKey);
                }
                AttachPlaylist(playlist);
                Playlists.Add(playlist);
            }

            try
            {
                OutputFolderPath = Path.GetFullPath(
                    string.IsNullOrWhiteSpace(state.OutputFolder)
                        ? AppPaths.DerivedTracks
                        : state.OutputFolder);
            }
            catch (Exception exception) when (exception is
                ArgumentException or
                NotSupportedException or
                PathTooLongException)
            {
                OutputFolderPath = AppPaths.DerivedTracks;
                _trackWorkspaceWarning = "La carpeta guardada no era válida; se restauró la predeterminada";
            }

            try
            {
                _trackLibrary.ScanFolder(OutputFolderPath);
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                ArgumentException or
                NotSupportedException)
            {
                _trackWorkspaceWarning = $"No se pudo escanear la carpeta de pistas: {exception.Message}";
            }

            SelectedPlaybackMode = PlaybackModeOptions.First(option => option.Mode == state.PlaybackMode);
            SelectedPlaylist = Playlists.FirstOrDefault(playlist => playlist.Id == state.SelectedPlaylistId);
        }
        finally
        {
            _isInitializingTrackWorkspace = false;
        }

        RefreshLibraryPresentation();
        if (SelectedPlaylist is null && !Playlists.Any(playlist => playlist.IsIncludedInMix))
        {
            ResetLocalPlaybackQueue(currentTrackId: null);
        }
        else
        {
            ResetPlaylistPlaybackQueue(currentItemId: null);
        }
        SaveTrackWorkspace(silent: true);
    }

    private void InitializeTrackCommands()
    {
        LoadTrackCommand = new RelayCommand<LocalTrack>(track =>
        {
            if (track is not null)
            {
                _ = LoadAndSelectTrackAsync(track, autoPlay: false, resetNavigation: true);
            }
        });
        RemoveLibraryTrackCommand = new RelayCommand<LocalTrack>(RemoveTrackFromLibrary);
        ChooseOutputFolderCommand = new RelayCommand(ChooseOutputFolder);
        RescanLibraryCommand = new RelayCommand(() => RescanOutputFolder(showStatus: true));
        CreatePlaylistCommand = new RelayCommand(CreatePlaylist);
        RenamePlaylistCommand = new RelayCommand(RenamePlaylist);
        DeletePlaylistCommand = new RelayCommand(DeletePlaylist);
        ClearPlaylistMixCommand = new RelayCommand(ClearPlaylistMix);
        PlayPlaylistQueueCommand = new RelayCommand(() => _ = PlayPlaylistQueueAsync());
        AddTrackToPlaylistCommand = new RelayCommand<LocalTrack>(AddTrackToPlaylist);
        PlayPlaylistItemCommand = new RelayCommand<PlaylistItemViewModel>(item =>
        {
            if (item is not null)
            {
                _ = PlayPlaylistItemAsync(item);
            }
        });
        RemovePlaylistItemCommand = new RelayCommand<PlaylistItemViewModel>(RemovePlaylistItem);
        MovePlaylistItemUpCommand = new RelayCommand<PlaylistItemViewModel>(item =>
            MovePlaylistItem(item, moveUp: true));
        MovePlaylistItemDownCommand = new RelayCommand<PlaylistItemViewModel>(item =>
            MovePlaylistItem(item, moveUp: false));
        PreviousTrackCommand = new RelayCommand(() => _ = NavigatePlaylistAsync(previous: true));
        NextTrackCommand = new RelayCommand(() => _ = NavigatePlaylistAsync(previous: false));
    }

    private async Task LoadAndSelectTrackAsync(
        LocalTrack track,
        bool autoPlay,
        bool resetNavigation,
        CancellationToken cancellationToken = default,
        string? navigationId = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (_currentYouTubeItem is not null)
        {
            YouTubeControlRequested?.Invoke(this, new YouTubeControlRequest(YouTubeControlAction.Pause));
        }
        var requestId = Interlocked.Increment(ref _trackLoadSequence);
        CancelPendingTrackLoad();
        _desiredTrackPlaying = false;
        _activeLoadGeneration = 0;
        _activeRunGeneration = 0;
        _audio.UnloadTrack();
        CurrentTrack = null;
        SelectedLibraryTrack = track;

        if (!File.Exists(track.Path))
        {
            track.IsMissing = true;
            _isTrackLoading = false;
            IsBusy = false;
            RefreshLibraryPresentation();
            SaveTrackWorkspace();
            StatusMessage = $"No se encuentra el archivo de {track.Title}";
            return;
        }

        track.IsMissing = false;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previousCancellation = Interlocked.Exchange(ref _trackLoadCancellation, cancellation);
        try
        {
            previousCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // La carga anterior terminó justo al sustituirla.
        }

        try
        {
            IsBusy = true;
            _isTrackLoading = true;
            StatusMessage = $"Cargando {track.Title}…";
            var loadGeneration = await _audio.LoadTrackAsync(track.Path, cancellation.Token);
            if (requestId != Volatile.Read(ref _trackLoadSequence))
            {
                return;
            }

            _activeLoadGeneration = loadGeneration;
            _currentYouTubeItem = null;
            CurrentTrack = track;
            SelectedLibraryTrack = track;
            var playlistSelection = PlaylistItems.FirstOrDefault(item =>
                item.Kind == PlaylistItemKind.LocalTrack &&
                string.Equals(item.Item.TrackId, track.Id, StringComparison.Ordinal));
            if (playlistSelection is not null)
            {
                SelectedPlaylistItem = playlistSelection;
            }

            if (resetNavigation)
            {
                ResetLocalPlaybackQueue(track.Id);
            }
            else if (navigationId is not null &&
                     !string.Equals(
                         _playbackNavigator.CurrentTrackId,
                         navigationId,
                         StringComparison.Ordinal))
            {
                _playbackNavigator.Select(navigationId);
            }

            var shouldAutoPlay = autoPlay && !cancellation.IsCancellationRequested;
            if (shouldAutoPlay)
            {
                _activeRunGeneration = _audio.PlayTrack();
                _desiredTrackPlaying = _activeRunGeneration != 0;
            }

            StatusMessage = shouldAutoPlay
                ? $"Reproduciendo {track.Title}"
                : $"Pista cargada: {track.Title}";
        }
        catch (OperationCanceledException) when (
            cancellation.IsCancellationRequested ||
            requestId != Volatile.Read(ref _trackLoadSequence))
        {
            // Una selección más reciente reemplazó esta carga.
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo abrir la pista: {exception.Message}";
        }
        finally
        {
            if (requestId == Volatile.Read(ref _trackLoadSequence))
            {
                _isTrackLoading = false;
                IsBusy = false;
            }

            Interlocked.CompareExchange(ref _trackLoadCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void ChooseOutputFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Elegir carpeta para las pistas generadas sin batería",
            InitialDirectory = Directory.Exists(OutputFolderPath) ? OutputFolderPath : AppPaths.DerivedTracks,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        OutputFolderPath = Path.GetFullPath(dialog.FolderName);
        RescanOutputFolder(showStatus: true);
    }

    private void RescanOutputFolder(bool showStatus)
    {
        try
        {
            var added = _trackLibrary.ScanFolder(OutputFolderPath).Count;
            RefreshLibraryPresentation();
            SaveTrackWorkspace();
            if (showStatus)
            {
                StatusMessage = added == 0
                    ? "Biblioteca actualizada; no se encontraron pistas nuevas"
                    : added == 1
                        ? "Biblioteca actualizada · 1 pista nueva"
                        : $"Biblioteca actualizada · {added} pistas nuevas";
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            StatusMessage = $"No se pudo escanear la carpeta: {exception.Message}";
        }
    }

    private void CreatePlaylist()
    {
        var name = PlaylistNameDraft.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Playlist {Playlists.Count + 1}";
        }

        var playlist = new Playlist { Id = Guid.NewGuid().ToString("N"), Name = name };
        AttachPlaylist(playlist);
        Playlists.Add(playlist);
        SelectedPlaylist = playlist;
        PlaylistNameDraft = playlist.Name;
        SaveTrackWorkspace();
        StatusMessage = $"Playlist creada: {playlist.Name}";
    }

    private void RenamePlaylist()
    {
        if (SelectedPlaylist is null)
        {
            StatusMessage = "Selecciona una playlist para renombrarla";
            return;
        }

        var name = PlaylistNameDraft.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            StatusMessage = "Escribe un nombre para la playlist";
            return;
        }

        SelectedPlaylist.Name = name;
        OnPropertyChanged(nameof(Playlists));
        OnPropertyChanged(nameof(MixedPlaylistSummary));
        SaveTrackWorkspace();
        StatusMessage = $"Playlist renombrada: {name}";
    }

    private void DeletePlaylist()
    {
        if (SelectedPlaylist is null)
        {
            StatusMessage = "Selecciona una playlist para eliminarla";
            return;
        }

        var deletedName = SelectedPlaylist.Name;
        var index = Playlists.IndexOf(SelectedPlaylist);
        DetachPlaylist(SelectedPlaylist);
        Playlists.Remove(SelectedPlaylist);
        SelectedPlaylist = Playlists.Count == 0
            ? null
            : Playlists[Math.Min(index, Playlists.Count - 1)];
        SaveTrackWorkspace();
        StatusMessage = $"Playlist eliminada: {deletedName} · ningún audio se ha borrado";
    }

    private void ClearPlaylistMix()
    {
        if (!Playlists.Any(playlist => playlist.IsIncludedInMix))
        {
            StatusMessage = "No hay playlists marcadas para la mezcla";
            return;
        }

        _isUpdatingPlaylistMix = true;
        try
        {
            foreach (var playlist in Playlists)
            {
                playlist.IsIncludedInMix = false;
            }
        }
        finally
        {
            _isUpdatingPlaylistMix = false;
        }

        ResetPlaylistPlaybackQueue(_playbackNavigator.CurrentTrackId);
        OnPropertyChanged(nameof(MixedPlaylistSummary));
        SaveTrackWorkspace();
        StatusMessage = SelectedPlaylist is null
            ? "Mezcla desactivada · se reproducirá la biblioteca completa"
            : $"Mezcla desactivada · se reproducirá {SelectedPlaylist.Name}";
    }

    private void AddTrackToPlaylist(LocalTrack? track)
    {
        if (track is null)
        {
            StatusMessage = "Selecciona primero una pista de la biblioteca";
            return;
        }
        if (SelectedPlaylist is null)
        {
            StatusMessage = "Crea o selecciona una playlist primero";
            return;
        }
        if (!PlaylistEditor.AddTrack(SelectedPlaylist, track))
        {
            StatusMessage = $"{track.Title} ya estaba en {SelectedPlaylist.Name}";
            return;
        }

        PlaylistChanged();
        StatusMessage = $"{track.Title} añadida a {SelectedPlaylist.Name}";
    }

    public bool AddYouTubeToSelectedPlaylist(
        string videoId,
        string url,
        string title,
        string? thumbnailUrl = null)
    {
        if (SelectedPlaylist is null)
        {
            StatusMessage = "Crea o selecciona una playlist antes de añadir el vídeo";
            return false;
        }
        if (!PlaylistEditor.AddYouTube(
                SelectedPlaylist,
                videoId,
                url,
                title,
                thumbnailUrl))
        {
            StatusMessage = $"{title} ya estaba en {SelectedPlaylist.Name}";
            return false;
        }

        var addedItem = SelectedPlaylist.Items.FirstOrDefault(item =>
            item.Kind == PlaylistItemKind.YouTube &&
            string.Equals(item.YouTubeVideoId, videoId, StringComparison.Ordinal));
        if (addedItem is not null)
        {
            addedItem.Tempo = _analysisDatabase.GetTempo(addedItem.MediaKey);
        }

        PlaylistChanged();
        StatusMessage = $"Vídeo añadido a {SelectedPlaylist.Name}: {title}";
        return true;
    }

    public YouTubePlaylistImportResult ImportYouTubePlaylist(
        IReadOnlyList<YouTubePlaylistEntry> entries,
        string playlistName)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (SelectedPlaylist is null)
        {
            StatusMessage = "Crea o selecciona una playlist antes de importar la lista de YouTube";
            return new YouTubePlaylistImportResult(entries.Count, 0, 0, playlistName);
        }

        var added = PlaylistEditor.AddYouTubeRange(SelectedPlaylist, entries);
        var importedIds = entries.Select(entry => entry.VideoId).ToHashSet(StringComparer.Ordinal);
        foreach (var item in SelectedPlaylist.Items.Where(item =>
                     item.Kind == PlaylistItemKind.YouTube &&
                     item.YouTubeVideoId is not null &&
                     importedIds.Contains(item.YouTubeVideoId)))
        {
            item.Tempo = _analysisDatabase.GetTempo(item.MediaKey);
        }

        if (added > 0)
        {
            PlaylistChanged();
        }

        var result = new YouTubePlaylistImportResult(
            entries.Count,
            added,
            entries.Count - added,
            playlistName);
        StatusMessage = added == 0
            ? $"La playlist de YouTube ya estaba incluida en {SelectedPlaylist.Name}"
            : $"{added} vídeos importados desde {playlistName} a {SelectedPlaylist.Name}";
        return result;
    }

    private void RemovePlaylistItem(PlaylistItemViewModel? item)
    {
        if (item is null || SelectedPlaylist is null ||
            !PlaylistEditor.RemoveItem(SelectedPlaylist, item.Id))
        {
            return;
        }

        PlaylistChanged();
        StatusMessage = $"{item.Title} quitado de la playlist · el origen sigue intacto";
    }

    private void RemoveTrackFromLibrary(LocalTrack? track)
    {
        if (track is null)
        {
            return;
        }

        if (CurrentTrack?.Id == track.Id)
        {
            StopTrack();
            CancelPendingTrackLoad();
            _audio.UnloadTrack();
            CurrentTrack = null;
        }

        foreach (var playlist in Playlists)
        {
            for (var index = playlist.Items.Count - 1; index >= 0; index--)
            {
                if (playlist.Items[index].Kind == PlaylistItemKind.LocalTrack &&
                    string.Equals(playlist.Items[index].TrackId, track.Id, StringComparison.Ordinal))
                {
                    playlist.Items.RemoveAt(index);
                }
            }
        }

        _analysisDatabase.Remove($"local:{track.Id}");
        if (!_trackLibrary.Remove(track.Id))
        {
            return;
        }

        if (SelectedLibraryTrack?.Id == track.Id)
        {
            SelectedLibraryTrack = null;
        }
        ResetPlaylistPlaybackQueue(currentItemId: null);
        RefreshLibraryPresentation();
        RefreshPerformanceHistory();
        SaveTrackWorkspace();
        StatusMessage = $"{track.Title} quitada de la biblioteca y datos de análisis borrados · el archivo no se ha eliminado";
    }

    private void MovePlaylistItem(PlaylistItemViewModel? item, bool moveUp)
    {
        if (item is null || SelectedPlaylist is null)
        {
            return;
        }
        var moved = moveUp
            ? PlaylistEditor.MoveUp(SelectedPlaylist, item.Id)
            : PlaylistEditor.MoveDown(SelectedPlaylist, item.Id);
        if (!moved)
        {
            return;
        }

        PlaylistChanged(item.Id);
    }

    public void AddTrackToSelectedPlaylist(LocalTrack track) => AddTrackToPlaylist(track);

    public void MoveSelectedPlaylistItem(PlaylistItemViewModel item, int targetIndex)
    {
        if (SelectedPlaylist is null ||
            !PlaylistEditor.MoveTo(SelectedPlaylist, item.Id, targetIndex))
        {
            return;
        }

        PlaylistChanged(item.Id);
    }

    public void PlayPlaylistItem(PlaylistItemViewModel item) =>
        _ = PlayPlaylistItemAsync(item);

    public void OpenYouTubePage() => Navigate("YouTube");

    private void PlaylistChanged(string? selectedItemId = null)
    {
        RebuildPlaylistItems();
        if (selectedItemId is not null)
        {
            SelectedPlaylistItem = PlaylistItems.FirstOrDefault(item => item.Id == selectedItemId);
        }
        ResetPlaylistPlaybackQueue(_playbackNavigator.CurrentTrackId);
        OnPropertyChanged(nameof(MixedPlaylistSummary));
        OnPropertyChanged(nameof(SelectedPlaylistSummary));
        SaveTrackWorkspace();
    }

    private async Task PlayPlaylistQueueAsync()
    {
        CancelPendingTrackLoad();
        _desiredTrackPlaying = false;
        _activeRunGeneration = 0;
        _audio.StopTrack();
        ResetPlaylistPlaybackQueue(currentItemId: null);

        var targetId = _playbackNavigator.NextManual();
        if (targetId is null)
        {
            StatusMessage = "La cola no contiene pistas disponibles";
            return;
        }

        await PlayNavigationTargetAsync(targetId, autoPlayLocal: true);
    }

    private async Task PlayPlaylistItemAsync(PlaylistItemViewModel item)
    {
        ResetPlaylistPlaybackQueue(item.Id);
        if (!_playbackNavigator.Select(item.Id))
        {
            StatusMessage = $"{item.Title} no está disponible";
            return;
        }

        await PlayNavigationTargetAsync(item.Id, autoPlayLocal: true);
    }

    private async Task NavigatePlaylistAsync(bool previous)
    {
        CancelPendingTrackLoad();
        _desiredTrackPlaying = false;
        _activeRunGeneration = 0;
        _audio.StopTrack();

        var targetId = previous
            ? _playbackNavigator.Previous()
            : _playbackNavigator.NextManual();
        if (targetId is null)
        {
            StatusMessage = previous ? "No hay una pista anterior" : "No hay una pista siguiente";
            return;
        }

        await PlayNavigationTargetAsync(targetId, autoPlayLocal: true);
    }

    private void CancelPendingTrackLoad()
    {
        try
        {
            _trackLoadCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // La carga terminó justo antes de la nueva intención del usuario.
        }
    }

    private async Task HandleNaturalTrackEndAsync(TrackEndedNotification notification)
    {
        if (notification.LoadGeneration != _activeLoadGeneration)
        {
            return;
        }

        FinishPerformanceEvaluation(naturalEnd: true);

        var nextId = _playbackNavigator.NextAutomatic();
        if (nextId is null)
        {
            StatusMessage = SelectedPlaybackMode?.Mode == PlaybackMode.Single
                ? "La pista ha terminado"
                : "La cola de reproducción ha terminado";
            return;
        }

        await PlayNavigationTargetAsync(nextId, autoPlayLocal: true);
    }

    public async Task HandleYouTubeEndedAsync(string videoId)
    {
        if (_currentYouTubeItem?.YouTubeVideoId != videoId)
        {
            return;
        }

        FinishPerformanceEvaluation(naturalEnd: true);

        var nextId = _playbackNavigator.NextAutomatic();
        if (nextId is null)
        {
            StatusMessage = SelectedPlaybackMode?.Mode == PlaybackMode.Single
                ? "El vídeo ha terminado"
                : "La cola de reproducción ha terminado";
            return;
        }

        await PlayNavigationTargetAsync(nextId, autoPlayLocal: true);
    }

    private async Task PlayNavigationTargetAsync(string navigationId, bool autoPlayLocal)
    {
        if (_playlistQueueActive &&
            _playlistPlaybackItems.TryGetValue(navigationId, out var playlistItem))
        {
            if (playlistItem.Kind == PlaylistItemKind.YouTube &&
                playlistItem.YouTubeUrl is not null)
            {
                if (IsRecordingOutput)
                {
                    await StopOutputRecordingAsync();
                }
                CancelPendingTrackLoad();
                _desiredTrackPlaying = false;
                _activeLoadGeneration = 0;
                _activeRunGeneration = 0;
                _audio.UnloadTrack();
                CurrentTrack = null;
                _currentYouTubeItem = playlistItem;
                OnCurrentYouTubeChanged(playlistItem);
                OnPropertyChanged(nameof(CanStartOutputRecording));
                OnPropertyChanged(nameof(CurrentTrackTitle));
                OnPropertyChanged(nameof(CurrentTrackSubtitle));
                OnPropertyChanged(nameof(HasTrack));
                StatusMessage = $"Cargando YouTube: {playlistItem.Title}";
                YouTubePlaybackRequested?.Invoke(
                    this,
                    new YouTubePlaybackRequest(
                        new Uri(playlistItem.YouTubeUrl),
                        playlistItem.YouTubeVideoId!,
                        playlistItem.Title));
                return;
            }

            if (playlistItem.TrackId is not null &&
                _trackLibrary.TryGetById(playlistItem.TrackId, out var localTrack) &&
                localTrack.IsAvailable)
            {
                await LoadAndSelectTrackAsync(
                    localTrack,
                    autoPlayLocal,
                    resetNavigation: false,
                    navigationId: navigationId);
                return;
            }
        }

        if (_trackLibrary.TryGetById(navigationId, out var libraryTrack) && libraryTrack.IsAvailable)
        {
            await LoadAndSelectTrackAsync(
                libraryTrack,
                autoPlayLocal,
                resetNavigation: false,
                navigationId: navigationId);
            return;
        }

        StatusMessage = "El elemento de la cola ya no está disponible";
    }

    public bool HandleYouTubePlaybackState(string? videoId, bool playing)
    {
        if (_currentYouTubeItem is null)
        {
            SetYouTubeAudioActive(playing);
            return false;
        }

        if (!string.Equals(
                _currentYouTubeItem.YouTubeVideoId,
                videoId,
                StringComparison.Ordinal))
        {
            return false;
        }

        SetYouTubeAudioActive(playing);
        StatusMessage = playing
            ? $"Reproduciendo YouTube: {_currentYouTubeItem.Title}"
            : $"YouTube en pausa: {_currentYouTubeItem.Title}";
        return true;
    }

    public void HandleYouTubePlaybackFailure(string? videoId, string? message)
    {
        if (_currentYouTubeItem is null ||
            !string.Equals(
                _currentYouTubeItem.YouTubeVideoId,
                videoId,
                StringComparison.Ordinal))
        {
            return;
        }

        SetYouTubeAudioActive(false);
        StatusMessage = string.IsNullOrWhiteSpace(message)
            ? $"YouTube no pudo reproducir {_currentYouTubeItem.Title}"
            : $"YouTube no pudo reproducir {_currentYouTubeItem.Title}: {message}";
    }

    private void RebuildPlaylistItems()
    {
        var selectedId = SelectedPlaylistItem?.Id;
        PlaylistItems.Clear();
        if (SelectedPlaylist is not null)
        {
            foreach (var item in SelectedPlaylist.Items)
            {
                LocalTrack? track = null;
                if (item.TrackId is not null)
                {
                    _trackLibrary.TryGetById(item.TrackId, out track!);
                }
                PlaylistItems.Add(new PlaylistItemViewModel { Item = item, LocalTrack = track });
            }
        }

        SelectedPlaylistItem = selectedId is null
            ? null
            : PlaylistItems.FirstOrDefault(item => item.Id == selectedId);
    }

    private bool IsPlaylistItemAvailable(PlaylistItem item) => item.Kind switch
    {
        PlaylistItemKind.YouTube => YouTubeNavigationService.IsYouTubeUri(
            Uri.TryCreate(item.YouTubeUrl, UriKind.Absolute, out var uri) ? uri : null),
        PlaylistItemKind.LocalTrack => item.TrackId is not null &&
                                       _trackLibrary.TryGetById(item.TrackId, out var track) &&
                                       track.IsAvailable,
        _ => false
    };

    private void ResetPlaylistPlaybackQueue(string? currentItemId)
    {
        var items = PlaylistMixService.BuildQueue(Playlists, SelectedPlaylist)
            .Where(IsPlaylistItemAvailable)
            .ToArray();
        _playlistPlaybackItems.Clear();
        foreach (var item in items)
        {
            _playlistPlaybackItems[item.Id] = item;
        }
        _playlistQueueActive = true;
        _playbackNavigator.SetQueue(items.Select(item => item.Id), currentItemId);
    }

    private void ResetLocalPlaybackQueue(string? currentTrackId)
    {
        _playlistPlaybackItems.Clear();
        _playlistQueueActive = false;
        var playableIds = Tracks
            .Where(track => track.IsAvailable)
            .Select(track => track.Id)
            .ToArray();
        _playbackNavigator.SetQueue(playableIds, currentTrackId);
    }

    private void AttachPlaylist(Playlist playlist) =>
        playlist.PropertyChanged += OnPlaylistPropertyChanged;

    private void DetachPlaylist(Playlist playlist) =>
        playlist.PropertyChanged -= OnPlaylistPropertyChanged;

    private void DetachAllPlaylists()
    {
        foreach (var playlist in Playlists)
        {
            DetachPlaylist(playlist);
        }
    }

    private void OnPlaylistPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(Playlist.Name))
        {
            OnPropertyChanged(nameof(MixedPlaylistSummary));
            return;
        }

        if (eventArgs.PropertyName != nameof(Playlist.IsIncludedInMix) ||
            _isUpdatingPlaylistMix)
        {
            return;
        }

        ResetPlaylistPlaybackQueue(_playbackNavigator.CurrentTrackId);
        OnPropertyChanged(nameof(MixedPlaylistSummary));
        if (!_isInitializingTrackWorkspace)
        {
            SaveTrackWorkspace();
        }
    }

    private void RefreshLibraryPresentation()
    {
        RebuildPlaylistItems();
        OnPropertyChanged(nameof(LibrarySummary));
        OnPropertyChanged(nameof(SelectedPlaylistSummary));
        OnPropertyChanged(nameof(CurrentTrackSubtitle));
        OnPropertyChanged(nameof(CanCreateDrumless));
    }

    private void SaveTrackWorkspace(bool silent = false)
    {
        if (_isInitializingTrackWorkspace)
        {
            return;
        }

        try
        {
            var state = new StudioState
            {
                OutputFolder = OutputFolderPath,
                SelectedPlaylistId = SelectedPlaylist?.Id,
                PlaybackMode = SelectedPlaybackMode?.Mode ?? PlaybackMode.Sequential,
                AudioOutputDeviceId = _preferredAudioOutputDeviceId,
                AudioInputOutputDeviceId = _preferredAudioInputOutputDeviceId,
                AudioInputChannelIndex = _preferredAudioInputChannelIndex,
                AudioInputGain = AudioInputGain,
                AudioInputMonitors = AudioInputMonitors.Count > 0
                    ? AudioInputMonitors
                        .Where(monitor => monitor.IsEnabled)
                        .Select(monitor => monitor.ToSetting())
                        .ToList()
                    : _preferredAudioInputMonitors.ToList(),
                AudioEffectBuses = AudioEffectBuses
                    .Select(bus => bus.ToSetting())
                    .ToList(),
                MidiDeviceName = _preferredMidiDeviceName,
                MidiDeviceIndex = _preferredMidiDeviceIndex,
                AutoConnectMidi = _autoConnectMidi,
                MidiVelocitySensitivity = MidiVelocitySensitivity,
                ActiveLibraryId = _preferredInternalLibraryId,
                ActiveKitId = _preferredInternalKitId,
                TrackVolume = TrackVolume,
                VstModulePath = _preferredVstModulePath,
                VstClassId = _preferredVstClassId,
                AutoLoadVst = _autoLoadVst,
                StemSelection = SelectedStemSelection,
                PerformanceLatencyCompensationMs = PerformanceLatencyCompensationMs
            };
            state.Tracks.AddRange(_trackLibrary.Snapshot());
            state.Playlists.AddRange(Playlists);
            state.AnalysisRecords.AddRange(_analysisDatabase.Snapshot());
            _studioStateStore.Save(state);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            NotSupportedException)
        {
            if (!silent)
            {
                StatusMessage = $"No se pudo guardar la biblioteca: {exception.Message}";
            }
        }
    }
}

public sealed record PlaybackModeOption(PlaybackMode Mode, string Label);
