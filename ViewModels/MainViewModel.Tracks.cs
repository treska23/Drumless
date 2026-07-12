using System.Collections.ObjectModel;
using DrumPracticeStudio.Audio;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using Microsoft.Win32;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    private readonly StudioStateStore _studioStateStore = new();
    private readonly TrackLibraryService _trackLibrary = new();
    private readonly PlaybackNavigator _playbackNavigator = new();
    private CancellationTokenSource? _trackLoadCancellation;
    private long _trackLoadSequence;
    private long _activeLoadGeneration;
    private long _activeRunGeneration;
    private bool _desiredTrackPlaying;
    private bool _isTrackLoading;
    private bool _isInitializingTrackWorkspace;
    private string? _trackWorkspaceWarning;

    private string _outputFolderPath = AppPaths.DerivedTracks;
    private LocalTrack? _selectedLibraryTrack;
    private Playlist? _selectedPlaylist;
    private LocalTrack? _selectedPlaylistTrack;
    private PlaybackModeOption? _selectedPlaybackMode;
    private string _playlistNameDraft = string.Empty;

    public ObservableCollection<Playlist> Playlists { get; }
    public ObservableCollection<LocalTrack> PlaylistTracks { get; }
    public ObservableCollection<PlaybackModeOption> PlaybackModeOptions { get; }

    public RelayCommand<LocalTrack> LoadTrackCommand { get; private set; } = null!;
    public RelayCommand ChooseOutputFolderCommand { get; private set; } = null!;
    public RelayCommand RescanLibraryCommand { get; private set; } = null!;
    public RelayCommand CreatePlaylistCommand { get; private set; } = null!;
    public RelayCommand RenamePlaylistCommand { get; private set; } = null!;
    public RelayCommand DeletePlaylistCommand { get; private set; } = null!;
    public RelayCommand<LocalTrack> AddTrackToPlaylistCommand { get; private set; } = null!;
    public RelayCommand<LocalTrack> RemovePlaylistTrackCommand { get; private set; } = null!;
    public RelayCommand<LocalTrack> MovePlaylistTrackUpCommand { get; private set; } = null!;
    public RelayCommand<LocalTrack> MovePlaylistTrackDownCommand { get; private set; } = null!;
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
            RebuildPlaylistTracks();
            ResetPlaybackQueue(value?.TrackIds, CurrentTrack?.Id);
            if (!_isInitializingTrackWorkspace)
            {
                SaveTrackWorkspace();
            }
        }
    }

    public LocalTrack? SelectedPlaylistTrack
    {
        get => _selectedPlaylistTrack;
        set => SetProperty(ref _selectedPlaylistTrack, value);
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

    private void InitializeTrackWorkspace()
    {
        _isInitializingTrackWorkspace = true;
        try
        {
            var state = _studioStateStore.Load();
            _trackWorkspaceWarning = _studioStateStore.LastLoadWarning;
            _trackLibrary.Load(state.Tracks);

            foreach (var playlist in state.Playlists)
            {
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
        ResetPlaybackQueue(SelectedPlaylist?.TrackIds, currentTrackId: null);
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
        ChooseOutputFolderCommand = new RelayCommand(ChooseOutputFolder);
        RescanLibraryCommand = new RelayCommand(() => RescanOutputFolder(showStatus: true));
        CreatePlaylistCommand = new RelayCommand(CreatePlaylist);
        RenamePlaylistCommand = new RelayCommand(RenamePlaylist);
        DeletePlaylistCommand = new RelayCommand(DeletePlaylist);
        AddTrackToPlaylistCommand = new RelayCommand<LocalTrack>(AddTrackToPlaylist);
        RemovePlaylistTrackCommand = new RelayCommand<LocalTrack>(RemovePlaylistTrack);
        MovePlaylistTrackUpCommand = new RelayCommand<LocalTrack>(MovePlaylistTrackUp);
        MovePlaylistTrackDownCommand = new RelayCommand<LocalTrack>(MovePlaylistTrackDown);
        PreviousTrackCommand = new RelayCommand(() => _ = NavigatePlaylistAsync(previous: true));
        NextTrackCommand = new RelayCommand(() => _ = NavigatePlaylistAsync(previous: false));
    }

    private async Task LoadAndSelectTrackAsync(
        LocalTrack track,
        bool autoPlay,
        bool resetNavigation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
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
            CurrentTrack = track;
            SelectedLibraryTrack = track;
            if (SelectedPlaylist?.TrackIds.Contains(track.Id) == true)
            {
                SelectedPlaylistTrack = track;
            }

            if (resetNavigation)
            {
                var preferredIds = SelectedPlaylist?.TrackIds;
                if (preferredIds is null || !preferredIds.Contains(track.Id))
                {
                    preferredIds = new ObservableCollection<string>(
                        Tracks.Where(candidate => candidate.IsAvailable).Select(candidate => candidate.Id));
                }

                ResetPlaybackQueue(preferredIds, track.Id);
            }
            else if (!string.Equals(_playbackNavigator.CurrentTrackId, track.Id, StringComparison.Ordinal))
            {
                _playbackNavigator.Select(track.Id);
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
        Playlists.Remove(SelectedPlaylist);
        SelectedPlaylist = Playlists.Count == 0
            ? null
            : Playlists[Math.Min(index, Playlists.Count - 1)];
        SaveTrackWorkspace();
        StatusMessage = $"Playlist eliminada: {deletedName} · ningún audio se ha borrado";
    }

    private void AddTrackToPlaylist(LocalTrack? track)
    {
        if (track is null)
        {
            return;
        }

        if (SelectedPlaylist is null)
        {
            StatusMessage = "Crea o selecciona una playlist primero";
            return;
        }

        if (!PlaylistEditor.AddTrack(SelectedPlaylist, track.Id))
        {
            StatusMessage = $"{track.Title} ya estaba en {SelectedPlaylist.Name}";
            return;
        }

        RebuildPlaylistTracks();
        ResetPlaybackQueue(SelectedPlaylist.TrackIds, CurrentTrack?.Id);
        SaveTrackWorkspace();
        StatusMessage = $"{track.Title} añadida a {SelectedPlaylist.Name}";
    }

    private void RemovePlaylistTrack(LocalTrack? track)
    {
        if (track is null || SelectedPlaylist is null ||
            !PlaylistEditor.RemoveTrack(SelectedPlaylist, track.Id))
        {
            return;
        }

        RebuildPlaylistTracks();
        ResetPlaybackQueue(SelectedPlaylist.TrackIds, CurrentTrack?.Id);
        SaveTrackWorkspace();
        StatusMessage = $"{track.Title} quitada de la playlist · el archivo sigue intacto";
    }

    private void MovePlaylistTrackUp(LocalTrack? track) => MovePlaylistTrack(track, moveUp: true);

    private void MovePlaylistTrackDown(LocalTrack? track) => MovePlaylistTrack(track, moveUp: false);

    private void MovePlaylistTrack(LocalTrack? track, bool moveUp)
    {
        if (track is null || SelectedPlaylist is null)
        {
            return;
        }

        var moved = moveUp
            ? PlaylistEditor.MoveUp(SelectedPlaylist, track.Id)
            : PlaylistEditor.MoveDown(SelectedPlaylist, track.Id);
        if (!moved)
        {
            return;
        }

        RebuildPlaylistTracks();
        SelectedPlaylistTrack = track;
        ResetPlaybackQueue(SelectedPlaylist.TrackIds, CurrentTrack?.Id);
        SaveTrackWorkspace();
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
        if (targetId is null || !_trackLibrary.TryGetById(targetId, out var track))
        {
            StatusMessage = previous ? "No hay una pista anterior" : "No hay una pista siguiente";
            return;
        }

        await LoadAndSelectTrackAsync(track, autoPlay: true, resetNavigation: false);
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

        var nextId = _playbackNavigator.NextAutomatic();
        if (nextId is null || !_trackLibrary.TryGetById(nextId, out var nextTrack))
        {
            StatusMessage = SelectedPlaybackMode?.Mode == PlaybackMode.Single
                ? "La pista ha terminado"
                : "La cola de reproducción ha terminado";
            return;
        }

        await LoadAndSelectTrackAsync(nextTrack, autoPlay: true, resetNavigation: false);
    }

    private void RebuildPlaylistTracks()
    {
        var selectedId = SelectedPlaylistTrack?.Id;
        PlaylistTracks.Clear();
        if (SelectedPlaylist is not null)
        {
            foreach (var trackId in SelectedPlaylist.TrackIds)
            {
                if (_trackLibrary.TryGetById(trackId, out var track))
                {
                    PlaylistTracks.Add(track);
                }
            }
        }

        SelectedPlaylistTrack = selectedId is null
            ? null
            : PlaylistTracks.FirstOrDefault(track => track.Id == selectedId);
    }

    private void ResetPlaybackQueue(IEnumerable<string>? preferredTrackIds, string? currentTrackId)
    {
        var ids = preferredTrackIds ?? Tracks.Select(track => track.Id);
        var playableIds = ids
            .Select(id => _trackLibrary.TryGetById(id, out var track) ? track : null)
            .Where(track => track?.IsAvailable == true)
            .Select(track => track!.Id)
            .ToArray();
        _playbackNavigator.SetQueue(playableIds, currentTrackId);
    }

    private void RefreshLibraryPresentation()
    {
        RebuildPlaylistTracks();
        OnPropertyChanged(nameof(LibrarySummary));
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
                PlaybackMode = SelectedPlaybackMode?.Mode ?? PlaybackMode.Sequential
            };
            state.Tracks.AddRange(_trackLibrary.Snapshot());
            state.Playlists.AddRange(Playlists);
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
