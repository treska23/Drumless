using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using DrumPracticeStudio.Audio;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Midi;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using Microsoft.Win32;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly FactoryContentService _contentService = new();
    private readonly UserKitStore _userKitStore = new();
    private readonly DrumLibraryImportService _libraryImport = new();
    private readonly Vst3InstrumentScanner _vstScanner = new();
    private readonly AudioOutputDeviceService _audioOutputDevices = new();
    private readonly DrumRemovalService _drumRemoval = new();
    private readonly AudioEngine _audio = new();
    private readonly MidiInputService _midi = new();
    private readonly MidiProfile _midiProfile;
    private readonly DispatcherTimer _transportTimer;
    private CancellationTokenSource? _kitLoadCancellation;
    private CancellationTokenSource? _drumRemovalCancellation;
    private CancellationTokenSource? _vstLoadCancellation;
    private int _kitLoadSequence;
    private bool _isSynchronizingKitSelection;
    private bool _hasScannedVstInstruments;

    private string _currentPage = "Practice";
    private SoundLibrary? _selectedLibrary;
    private DrumKit? _selectedKit;
    private DrumKit? _activeKit;
    private MidiDeviceItem? _selectedMidiDevice;
    private AudioOutputDeviceItem? _selectedAudioOutputDevice;
    private LocalTrack? _currentTrack;
    private string _statusMessage = "Preparando el estudio…";
    private string _midiStatus = "No conectado";
    private string _playButtonLabel = "Reproducir";
    private string _trackPositionLabel = "00:00";
    private string _trackDurationLabel = "00:00";
    private double _trackProgress;
    private double _trackDurationSeconds = 1d;
    private double _trackVolume = 0.8d;
    private bool _isBusy;
    private bool _isRemovingDrums;
    private bool _isRemovalIndeterminate;
    private double _removalProgress;
    private string _removalStatus = "Selecciona una pista original para crear una copia sin batería.";
    private string _removalEngineStatus = "Motor local no instalado";
    private Vst3InstrumentItem? _selectedVstInstrument;
    private string _vstStatus = "Pulsa «Buscar instrumentos» para localizar Addictive Drums y Groove Agent de forma aislada.";
    private bool _isScanningVst;
    private string _audioOutputStatus = "Buscando salidas de audio…";

    public MainViewModel()
    {
        Libraries = _contentService.Load();
        var userLibrary = Libraries.First(library => library.Id == "user.sounds");
        foreach (var userKit in _userKitStore.Load())
        {
            userLibrary.Kits.Add(userKit);
        }
        ActivePads = [];
        MidiDevices = [];
        AudioOutputDevices = [];
        MappingRows = [];
        Vst3Instruments = [];
        Tracks = _trackLibrary.Tracks;
        Playlists = [];
        PlaylistTracks = [];
        PlaybackModeOptions =
        [
            new PlaybackModeOption(PlaybackMode.Single, "Una pista"),
            new PlaybackModeOption(PlaybackMode.Sequential, "Secuencial"),
            new PlaybackModeOption(PlaybackMode.Shuffle, "Aleatorio · sin repeticiones")
        ];
        InitializeTrackWorkspace();

        _midiProfile = CreateGeneralDrumProfile();
        BuildMappingRows();

        NavigateCommand = new RelayCommand<string>(page => Navigate(page ?? "Practice"));
        ActivateKitCommand = new RelayCommand<DrumKit>(kit =>
        {
            if (kit is not null)
            {
                _ = ActivateKitAsync(kit);
            }
        });
        PlayPadCommand = new RelayCommand<PadViewModel>(pad =>
        {
            if (pad is not null)
            {
                TriggerPad(pad, 112);
            }
        });
        ImportSampleCommand = new RelayCommand<PadViewModel>(pad =>
        {
            if (pad is not null)
            {
                _ = ImportSampleAsync(pad);
            }
        });
        ImportSoundLibraryCommand = new RelayCommand(() => _ = ImportSoundLibraryZipAsync());
        ImportSoundFolderCommand = new RelayCommand(() => _ = ImportSoundLibraryFolderAsync());
        ImportTrackCommand = new RelayCommand<string>(variant => _ = ImportTrackAsync(variant));
        ToggleTrackCommand = new RelayCommand(ToggleTrack);
        StopTrackCommand = new RelayCommand(StopTrack);
        SeekTrackCommand = new RelayCommand<string>(SeekTrack);
        CreateDrumlessCommand = new RelayCommand(() => _ = CreateDrumlessAsync());
        CancelDrumRemovalCommand = new RelayCommand(CancelDrumRemoval);
        RefreshMidiCommand = new RelayCommand(RefreshMidiDevices);
        ConnectMidiCommand = new RelayCommand(ToggleMidiConnection);
        RefreshAudioOutputsCommand = new RelayCommand(RefreshAudioOutputDevices);
        ApplyAudioOutputCommand = new RelayCommand(ApplyAudioOutputDevice);
        ScanVstInstrumentsCommand = new RelayCommand(() => _ = ScanVstInstrumentsAsync(force: true));
        LoadVstInstrumentCommand = new RelayCommand(() => _ = LoadSelectedVstInstrumentAsync());
        UseInternalDrumsCommand = new RelayCommand(UseInternalDrums);
        OpenVstEditorCommand = new RelayCommand(OpenVstEditor);
        PanicVstCommand = new RelayCommand(() => _audio.PanicVstInstrument());
        InitializeTrackCommands();

        _midi.NoteReceived += OnMidiNoteReceived;
        _midi.NoteOffReceived += OnMidiNoteOffReceived;
        _midi.ControlChangeReceived += OnMidiControlChangeReceived;
        _midi.Error += (_, message) => PostStatus($"MIDI: {message}");
        _audio.VstInstrumentExited += OnVstInstrumentExited;

        _transportTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _transportTimer.Tick += (_, _) => RefreshTransport();
        _transportTimer.Start();

        RefreshMidiDevices();
        RefreshAudioOutputDevices();
        SelectedLibrary = Libraries.FirstOrDefault(library => library.Kits.Count > 0);

        StatusMessage = _audio.IsAvailable
            ? $"Listo · {_audio.Status}"
            : _audio.Status;
        if (!string.IsNullOrWhiteSpace(_trackWorkspaceWarning))
        {
            StatusMessage = $"{StatusMessage} · {_trackWorkspaceWarning}";
        }
        RemovalEngineStatus = _drumRemoval.IsInstalled
            ? "Demucs local preparado"
            : "Demucs local se instalará al usarlo por primera vez";
    }

    public ObservableCollection<SoundLibrary> Libraries { get; }
    public ObservableCollection<PadViewModel> ActivePads { get; }
    public ObservableCollection<MidiDeviceItem> MidiDevices { get; }
    public ObservableCollection<AudioOutputDeviceItem> AudioOutputDevices { get; }
    public ObservableCollection<MidiMappingRow> MappingRows { get; }
    public ObservableCollection<Vst3InstrumentItem> Vst3Instruments { get; }
    public ObservableCollection<LocalTrack> Tracks { get; }

    public RelayCommand<string> NavigateCommand { get; }
    public RelayCommand<DrumKit> ActivateKitCommand { get; }
    public RelayCommand<PadViewModel> PlayPadCommand { get; }
    public RelayCommand<PadViewModel> ImportSampleCommand { get; }
    public RelayCommand ImportSoundLibraryCommand { get; }
    public RelayCommand ImportSoundFolderCommand { get; }
    public RelayCommand<string> ImportTrackCommand { get; }
    public RelayCommand ToggleTrackCommand { get; }
    public RelayCommand StopTrackCommand { get; }
    public RelayCommand<string> SeekTrackCommand { get; }
    public RelayCommand CreateDrumlessCommand { get; }
    public RelayCommand CancelDrumRemovalCommand { get; }
    public RelayCommand RefreshMidiCommand { get; }
    public RelayCommand ConnectMidiCommand { get; }
    public RelayCommand RefreshAudioOutputsCommand { get; }
    public RelayCommand ApplyAudioOutputCommand { get; }
    public RelayCommand ScanVstInstrumentsCommand { get; }
    public RelayCommand LoadVstInstrumentCommand { get; }
    public RelayCommand UseInternalDrumsCommand { get; }
    public RelayCommand OpenVstEditorCommand { get; }
    public RelayCommand PanicVstCommand { get; }

    public SoundLibrary? SelectedLibrary
    {
        get => _selectedLibrary;
        set
        {
            if (SetProperty(ref _selectedLibrary, value))
            {
                SelectedKit = value?.Kits.FirstOrDefault();
                OnPropertyChanged(nameof(HasSelectedLibraryKits));
                if (!_isSynchronizingKitSelection && SelectedKit is not null)
                {
                    _ = ActivateKitAsync(SelectedKit);
                }
            }
        }
    }

    public DrumKit? SelectedKit
    {
        get => _selectedKit;
        set => SetProperty(ref _selectedKit, value);
    }

    public DrumKit? ActiveKit
    {
        get => _activeKit;
        private set
        {
            if (SetProperty(ref _activeKit, value))
            {
                OnPropertyChanged(nameof(ActiveKitName));
                OnPropertyChanged(nameof(ActiveLibraryName));
                OnPropertyChanged(nameof(HasActiveKit));
            }
        }
    }

    public MidiDeviceItem? SelectedMidiDevice
    {
        get => _selectedMidiDevice;
        set => SetProperty(ref _selectedMidiDevice, value);
    }

    public AudioOutputDeviceItem? SelectedAudioOutputDevice
    {
        get => _selectedAudioOutputDevice;
        set => SetProperty(ref _selectedAudioOutputDevice, value);
    }

    public string AudioOutputStatus
    {
        get => _audioOutputStatus;
        private set => SetProperty(ref _audioOutputStatus, value);
    }

    public LocalTrack? CurrentTrack
    {
        get => _currentTrack;
        private set
        {
            if (SetProperty(ref _currentTrack, value))
            {
                OnPropertyChanged(nameof(CurrentTrackTitle));
                OnPropertyChanged(nameof(CurrentTrackSubtitle));
                OnPropertyChanged(nameof(HasTrack));
                OnPropertyChanged(nameof(CanCreateDrumless));
            }
        }
    }

    public string CurrentPage
    {
        get => _currentPage;
        private set => SetProperty(ref _currentPage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string MidiStatus
    {
        get => _midiStatus;
        private set
        {
            if (SetProperty(ref _midiStatus, value))
            {
                OnPropertyChanged(nameof(MidiButtonLabel));
            }
        }
    }

    public string PlayButtonLabel
    {
        get => _playButtonLabel;
        private set => SetProperty(ref _playButtonLabel, value);
    }

    public string TrackPositionLabel
    {
        get => _trackPositionLabel;
        private set => SetProperty(ref _trackPositionLabel, value);
    }

    public string TrackDurationLabel
    {
        get => _trackDurationLabel;
        private set => SetProperty(ref _trackDurationLabel, value);
    }

    public double TrackProgress
    {
        get => _trackProgress;
        set
        {
            var boundedPosition = Math.Clamp(value, 0d, TrackDurationSeconds);
            if (!SetProperty(ref _trackProgress, boundedPosition))
            {
                return;
            }

            var runGeneration = _audio.SeekTrack(TimeSpan.FromSeconds(boundedPosition));
            if (_desiredTrackPlaying)
            {
                _activeRunGeneration = runGeneration;
            }

            TrackPositionLabel = FormatTime(TimeSpan.FromSeconds(boundedPosition));
        }
    }

    public double TrackDurationSeconds
    {
        get => _trackDurationSeconds;
        private set => SetProperty(ref _trackDurationSeconds, Math.Max(1d, value));
    }

    public double TrackVolume
    {
        get => _trackVolume;
        set
        {
            if (SetProperty(ref _trackVolume, value))
            {
                _audio.SetTrackVolume((float)value);
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public bool IsRemovingDrums
    {
        get => _isRemovingDrums;
        private set
        {
            if (SetProperty(ref _isRemovingDrums, value))
            {
                OnPropertyChanged(nameof(CanCreateDrumless));
            }
        }
    }

    public bool IsRemovalIndeterminate
    {
        get => _isRemovalIndeterminate;
        private set => SetProperty(ref _isRemovalIndeterminate, value);
    }

    public double RemovalProgress
    {
        get => _removalProgress;
        private set => SetProperty(ref _removalProgress, value);
    }

    public string RemovalStatus
    {
        get => _removalStatus;
        private set => SetProperty(ref _removalStatus, value);
    }

    public string RemovalEngineStatus
    {
        get => _removalEngineStatus;
        private set => SetProperty(ref _removalEngineStatus, value);
    }

    public Vst3InstrumentItem? SelectedVstInstrument
    {
        get => _selectedVstInstrument;
        set => SetProperty(ref _selectedVstInstrument, value);
    }

    public string VstStatus
    {
        get => _vstStatus;
        private set => SetProperty(ref _vstStatus, value);
    }

    public bool IsScanningVst
    {
        get => _isScanningVst;
        private set => SetProperty(ref _isScanningVst, value);
    }

    public bool IsVstInstrumentLoaded => _audio.IsVstInstrumentLoaded;
    public string ActiveDrumEngineLabel => _audio.IsVstInstrumentLoaded
        ? $"VST3 · {_audio.VstInstrumentName}"
        : "Motor interno · kits WAV";

    public bool IsPracticePage => CurrentPage == "Practice";
    public bool IsLibrariesPage => CurrentPage == "Libraries";
    public bool IsTracksPage => CurrentPage == "Tracks";
    public bool IsYouTubePage => CurrentPage == "YouTube";
    public bool IsSettingsPage => CurrentPage == "Settings";
    public bool HasTrack => CurrentTrack is not null;
    public bool HasActiveKit => ActiveKit is not null;
    public bool HasSelectedLibraryKits => SelectedLibrary?.Kits.Count > 0;
    public bool CanCreateDrumless =>
        CurrentTrack?.Variant == TrackVariant.Original &&
        CurrentTrack.IsAvailable &&
        !IsRemovingDrums;
    public string ActiveKitName => ActiveKit?.Name ?? "Ningún kit";
    public string ActiveLibraryName => Libraries.FirstOrDefault(library => library.Id == ActiveKit?.LibraryId)?.Name ?? "Sin librería";
    public string CurrentTrackTitle => CurrentTrack?.Title ?? "Sin pista cargada";
    public string CurrentTrackSubtitle => CurrentTrack is null
        ? "Importa una pista original o una pista ya drumless"
        : CurrentTrack.IsMissing
            ? $"Archivo no encontrado · {CurrentTrack.Path}"
            : CurrentTrack.VariantLabel;
    public string MidiButtonLabel => _midi.IsConnected ? "Desconectar" : "Conectar";

    public void Dispose()
    {
        _transportTimer.Stop();
        _kitLoadCancellation?.Cancel();
        _kitLoadCancellation?.Dispose();
        _drumRemovalCancellation?.Cancel();
        _drumRemovalCancellation?.Dispose();
        _vstLoadCancellation?.Cancel();
        _vstLoadCancellation?.Dispose();
        _trackLoadCancellation?.Cancel();
        _trackLoadCancellation?.Dispose();
        SaveTrackWorkspace(silent: true);
        _audio.VstInstrumentExited -= OnVstInstrumentExited;
        _midi.NoteReceived -= OnMidiNoteReceived;
        _midi.NoteOffReceived -= OnMidiNoteOffReceived;
        _midi.ControlChangeReceived -= OnMidiControlChangeReceived;
        _midi.Dispose();
        _audio.Dispose();
    }

    private async Task ActivateKitAsync(DrumKit kit)
    {
        var requestId = Interlocked.Increment(ref _kitLoadSequence);
        var instrument = SelectedVstInstrument;
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _kitLoadCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();

        try
        {
            IsBusy = true;
            StatusMessage = $"Cargando {kit.Name}…";
            await _audio.LoadKitAsync(kit, cancellation.Token);
            if (requestId != Volatile.Read(ref _kitLoadSequence))
            {
                return;
            }

            ActiveKit = kit;
            ActivePads.Clear();
            foreach (var pad in kit.Pads)
            {
                ActivePads.Add(new PadViewModel(pad));
            }

            _isSynchronizingKitSelection = true;
            try
            {
                SelectedLibrary = Libraries.FirstOrDefault(library => library.Id == kit.LibraryId) ?? SelectedLibrary;
                SelectedKit = kit;
            }
            finally
            {
                _isSynchronizingKitSelection = false;
            }
            StatusMessage = $"Kit activo: {kit.Name} · el perfil MIDI no ha cambiado";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Una selección posterior reemplazó esta carga.
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo cargar el kit: {exception.Message}";
        }
        finally
        {
            if (requestId == Volatile.Read(ref _kitLoadSequence))
            {
                IsBusy = false;
            }

            Interlocked.CompareExchange(ref _kitLoadCancellation, null, cancellation);
            cancellation.Dispose();
        }
    }

    private void TriggerPad(PadViewModel pad, int velocity)
    {
        _audio.Trigger(pad.Articulation, velocity, pad.Pad.DefaultMidiNote);
        if (_audio.IsVstInstrumentLoaded)
        {
            _ = ReleaseClickedVstNoteAsync(pad.Pad.DefaultMidiNote);
        }
        _ = pad.FlashAsync();
    }

    private async Task ReleaseClickedVstNoteAsync(int midiNote)
    {
        await Task.Delay(120);
        _audio.SendNoteOff(midiNote, 0);
    }

    private async Task ImportSampleAsync(PadViewModel sourcePad)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Sustituir {sourcePad.Name}",
            Filter = "Sample WAV (*.wav)|*.wav",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true || ActiveKit is null)
        {
            return;
        }

        var managedSamplePath = _userKitStore.ImportSample(dialog.FileName);
        var targetKit = ActiveKit;
        if (targetKit.IsFactory)
        {
            targetKit = _contentService.CloneAsUserKit(targetKit, $"{targetKit.Name} · personalizado");
            var userLibrary = Libraries.First(library => library.Id == "user.sounds");
            userLibrary.Kits.Add(targetKit);
            OnPropertyChanged(nameof(Libraries));
        }

        var targetPad = targetKit.Pads.First(pad => pad.Id == sourcePad.Id);
        targetPad.Layers.Clear();
        var layer = new SampleLayer { MinVelocity = 1, MaxVelocity = 127 };
        layer.Samples.Add(new SampleReference(managedSamplePath));
        targetPad.Layers.Add(layer);

        var customLibrary = Libraries.First(library => library.Id == "user.sounds");
        _userKitStore.Save(customLibrary.Kits);

        await ActivateKitAsync(targetKit);
        StatusMessage = $"{targetPad.Name} usa ahora {Path.GetFileName(dialog.FileName)} · guardado en Mis sonidos";
    }

    private async Task ImportSoundLibraryZipAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Selecciona una librería de batería comprimida",
            Filter = "Librería ZIP (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ImportSoundLibraryAsync(() =>
            _libraryImport.ImportZip(dialog.FileName, _userKitStore.ImportSample));
    }

    private async Task ImportSoundLibraryFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Selecciona la carpeta de muestras WAV",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        await ImportSoundLibraryAsync(() =>
            _libraryImport.ImportFolder(dialog.FolderName, _userKitStore.ImportSample));
    }

    private async Task ImportSoundLibraryAsync(Func<DrumLibraryImportResult> import)
    {
        try
        {
            IsBusy = true;
            StatusMessage = "Importando librería de batería…";
            var result = import();
            var userLibrary = Libraries.First(library => library.Id == "user.sounds");
            userLibrary.Kits.Add(result.Kit);
            _userKitStore.Save(userLibrary.Kits);

            _isSynchronizingKitSelection = true;
            try
            {
                SelectedLibrary = userLibrary;
                SelectedKit = result.Kit;
            }
            finally
            {
                _isSynchronizingKitSelection = false;
            }

            await ActivateKitAsync(result.Kit);
            StatusMessage = result.SkippedFiles == 0
                ? $"Librería importada: {result.Kit.Name} · {result.ImportedFiles} muestras"
                : $"Librería importada: {result.Kit.Name} · {result.ImportedFiles} muestras · " +
                  $"{result.SkippedFiles} WAV sin reconocer";
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo importar la librería: {exception.Message}";
            MessageBox.Show(
                exception.Message,
                "Importar librería",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportTrackAsync(string? variantValue)
    {
        var dialog = new OpenFileDialog
        {
            Title = variantValue == "drumless" ? "Añadir pista ya sin batería" : "Importar pista local",
            Filter = "Audio (*.wav;*.mp3;*.flac;*.aiff)|*.wav;*.mp3;*.flac;*.aiff|Todos los archivos (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "Preparando la pista local…";
            var track = _trackLibrary.RegisterImported(
                dialog.FileName,
                variantValue == "drumless" ? TrackVariant.UserDrumless : TrackVariant.Original);
            SaveTrackWorkspace();
            RefreshLibraryPresentation();
            await LoadAndSelectTrackAsync(track, autoPlay: false, resetNavigation: true);
            StatusMessage = track.Variant == TrackVariant.UserDrumless
                ? "Pista drumless añadida; no se procesará"
                : "Pista original añadida; solo se quitará la batería si tú lo pides";
        }
        catch (Exception exception)
        {
            StatusMessage = $"No se pudo abrir la pista: {exception.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ToggleTrack()
    {
        if (_isTrackLoading)
        {
            StatusMessage = "Espera a que termine de cargarse la pista";
            return;
        }

        if (CurrentTrack is null)
        {
            _ = ImportTrackAsync("original");
            return;
        }

        if (CurrentTrack.IsMissing || !File.Exists(CurrentTrack.Path))
        {
            CurrentTrack.IsMissing = true;
            RefreshLibraryPresentation();
            SaveTrackWorkspace();
            StatusMessage = $"No se encuentra el archivo de {CurrentTrack.Title}";
            return;
        }

        if (_desiredTrackPlaying)
        {
            _desiredTrackPlaying = false;
            _activeRunGeneration = 0;
            _audio.PauseTrack();
        }
        else
        {
            _activeRunGeneration = _audio.PlayTrack();
            _desiredTrackPlaying = _activeRunGeneration != 0;
        }
    }

    private void StopTrack()
    {
        try
        {
            _trackLoadCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // La carga terminó justo antes de la orden de parada.
        }

        _desiredTrackPlaying = false;
        _activeRunGeneration = 0;
        _audio.StopTrack();
        PlayButtonLabel = "Reproducir";
    }

    private void SeekTrack(string? secondsValue)
    {
        if (!double.TryParse(secondsValue, out var delta))
        {
            return;
        }

        var target = _audio.TrackPosition + TimeSpan.FromSeconds(delta);
        var runGeneration = _audio.SeekTrack(target);
        if (_desiredTrackPlaying)
        {
            _activeRunGeneration = runGeneration;
        }
    }

    private async Task CreateDrumlessAsync()
    {
        if (CurrentTrack is null)
        {
            StatusMessage = "Primero importa una pista local";
            return;
        }

        if (CurrentTrack.Variant != TrackVariant.Original)
        {
            StatusMessage = "La pista seleccionada ya es drumless; no se procesará otra vez";
            return;
        }

        if (CurrentTrack.IsMissing || !File.Exists(CurrentTrack.Path))
        {
            CurrentTrack.IsMissing = true;
            RefreshLibraryPresentation();
            SaveTrackWorkspace();
            StatusMessage = "El archivo original ha desaparecido; no se puede separar";
            return;
        }

        if (IsRemovingDrums)
        {
            return;
        }

        var sourceTrack = CurrentTrack;
        _drumRemovalCancellation = new CancellationTokenSource();
        var cancellationToken = _drumRemovalCancellation.Token;
        var progress = new Progress<DrumRemovalProgress>(UpdateRemovalProgress);

        try
        {
            IsRemovingDrums = true;
            IsBusy = true;
            RemovalProgress = 0d;

            if (!_drumRemoval.IsInstalled)
            {
                var answer = MessageBox.Show(
                    "Para quitar la batería hay que instalar una vez Demucs, Python y PyTorch en una carpeta privada de la aplicación. " +
                    "La descarga es grande (aproximadamente 1–2 GB), no instala paquetes globales y puede tardar varios minutos.\n\n¿Instalar ahora?",
                    "Instalar motor de separación",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (answer != MessageBoxResult.Yes)
                {
                    RemovalStatus = "Instalación cancelada por el usuario";
                    return;
                }

                RemovalEngineStatus = "Instalando Demucs local…";
                await _drumRemoval.InstallAsync(progress, cancellationToken);
                RemovalEngineStatus = "Demucs local preparado";
            }

            _desiredTrackPlaying = false;
            _activeRunGeneration = 0;
            _audio.StopTrack();
            StatusMessage = $"Quitando batería de {sourceTrack.Title}…";
            var result = await _drumRemoval.CreateDrumlessAsync(
                sourceTrack,
                OutputFolderPath,
                progress,
                cancellationToken);
            var generatedTrack = _trackLibrary.RegisterGenerated(
                result.DrumlessPath,
                $"{sourceTrack.Title} · sin batería");
            SaveTrackWorkspace();
            RefreshLibraryPresentation();

            if (CurrentTrack?.Id == sourceTrack.Id)
            {
                await LoadAndSelectTrackAsync(
                    generatedTrack,
                    autoPlay: false,
                    resetNavigation: true,
                    cancellationToken);
            }

            RemovalProgress = 1d;
            RemovalStatus = "Versión sin batería creada y añadida a la biblioteca";
            StatusMessage = $"Lista: {generatedTrack.Title}";
        }
        catch (OperationCanceledException)
        {
            RemovalStatus = "Separación cancelada; el original no se ha modificado";
            StatusMessage = "Separación cancelada";
        }
        catch (Exception exception)
        {
            RemovalStatus = $"Error: {exception.Message}";
            StatusMessage = "No se pudo crear la versión sin batería";
        }
        finally
        {
            IsRemovingDrums = false;
            IsBusy = false;
            _drumRemovalCancellation?.Dispose();
            _drumRemovalCancellation = null;
        }
    }

    private void CancelDrumRemoval() => _drumRemovalCancellation?.Cancel();

    private void UpdateRemovalProgress(DrumRemovalProgress progress)
    {
        RemovalStatus = progress.Message;
        IsRemovalIndeterminate = progress.Percent is null;
        if (progress.Percent is not null)
        {
            RemovalProgress = Math.Clamp(progress.Percent.Value, 0d, 1d);
        }
    }

    private void RefreshTransport()
    {
        while (_audio.TryDequeueTrackEnded(out var ended))
        {
            if (_desiredTrackPlaying &&
                ended.LoadGeneration == _activeLoadGeneration &&
                ended.RunGeneration == _activeRunGeneration)
            {
                _desiredTrackPlaying = false;
                _activeRunGeneration = 0;
                _ = HandleNaturalTrackEndAsync(ended);
            }
        }

        var position = _audio.TrackPosition;
        var duration = _audio.TrackDuration;
        TrackPositionLabel = FormatTime(position);
        TrackDurationLabel = FormatTime(duration);
        SetProperty(ref _trackProgress, position.TotalSeconds, nameof(TrackProgress));
        TrackDurationSeconds = duration.TotalSeconds;
        PlayButtonLabel = _desiredTrackPlaying ? "Pausar" : "Reproducir";
    }

    private void RefreshMidiDevices()
    {
        MidiDevices.Clear();
        foreach (var device in _midi.GetDevices())
        {
            MidiDevices.Add(device);
        }

        SelectedMidiDevice = MidiDevices.FirstOrDefault();
        MidiStatus = MidiDevices.Count == 0 ? "No se encontraron entradas MIDI" : "Listo para conectar";
    }

    private void RefreshAudioOutputDevices()
    {
        try
        {
            var selectedId = SelectedAudioOutputDevice?.Id ?? _audio.OutputDeviceId;
            AudioOutputDevices.Clear();
            foreach (var device in _audioOutputDevices.GetDevices())
            {
                AudioOutputDevices.Add(device);
            }

            SelectedAudioOutputDevice = AudioOutputDevices.FirstOrDefault(device =>
                                            string.Equals(device.Id, selectedId, StringComparison.Ordinal))
                                        ?? AudioOutputDevices.FirstOrDefault(device => device.IsDefault)
                                        ?? AudioOutputDevices.FirstOrDefault();
            AudioOutputStatus = AudioOutputDevices.Count == 0
                ? "No se encontraron salidas de audio activas."
                : $"Salida activa: {_audio.OutputDeviceName ?? "predeterminada de Windows"}";
        }
        catch (Exception exception)
        {
            AudioOutputStatus = $"No se pudieron leer las salidas: {exception.Message}";
        }
    }

    private void ApplyAudioOutputDevice()
    {
        if (SelectedAudioOutputDevice is not { } selected)
        {
            AudioOutputStatus = "Selecciona una salida de audio.";
            return;
        }

        try
        {
            _audio.SelectOutputDevice(selected.Id);
            AudioOutputStatus = $"Salida activa: {selected.Name}";
            StatusMessage = $"El programa y el VST3 suenan por {selected.Name}";
            if (_audio.IsVstInstrumentLoaded)
            {
                VstStatus = $"Activo: {_audio.VstInstrumentName} · salida {selected.Name}";
            }
        }
        catch (Exception exception)
        {
            AudioOutputStatus = $"No se pudo usar {selected.Name}: {exception.Message}";
            MessageBox.Show(
                $"No se pudo abrir la salida «{selected.Name}».\n\n{exception.Message}\n\n" +
                "La salida anterior sigue activa.",
                "Salida de audio",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ToggleMidiConnection()
    {
        if (_midi.IsConnected)
        {
            _midi.Disconnect();
            MidiStatus = "No conectado";
            return;
        }

        if (SelectedMidiDevice is null)
        {
            MidiStatus = "Selecciona una entrada MIDI";
            return;
        }

        try
        {
            _midi.Connect(SelectedMidiDevice.Index);
            MidiStatus = $"Conectado a {SelectedMidiDevice.Name}";
            StatusMessage = "MIDI conectado · toca un pad para probar el kit activo";
        }
        catch (Exception exception)
        {
            MidiStatus = $"Error: {exception.Message}";
        }
    }

    private void OnMidiNoteReceived(object? sender, MidiNoteMessage message)
    {
        var hasMapping = _midiProfile.TryResolve(message.Note, out var articulation);
        if (!hasMapping && !_audio.IsVstInstrumentLoaded)
        {
            return;
        }

        _audio.Trigger(articulation ?? string.Empty, message.Velocity, message.Note, message.Channel);
        if (!hasMapping)
        {
            return;
        }

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var pad = ActivePads.FirstOrDefault(candidate =>
                string.Equals(candidate.Articulation, articulation, StringComparison.OrdinalIgnoreCase));
            if (pad is not null)
            {
                _ = pad.FlashAsync();
            }
        });
    }

    private void OnMidiNoteOffReceived(object? sender, MidiNoteOffMessage message) =>
        _audio.SendNoteOff(message.Note, message.Velocity, message.Channel);

    private void OnMidiControlChangeReceived(object? sender, MidiControlChangeMessage message) =>
        _audio.SendControlChange(message.Controller, message.Value);

    private async Task ScanVstInstrumentsAsync(bool force)
    {
        if (IsScanningVst || (_hasScannedVstInstruments && !force))
        {
            return;
        }

        try
        {
            IsScanningVst = true;
            VstStatus = "Buscando instrumentos VST3 instalados…";
            var result = await _vstScanner.ScanAsync();
            Vst3Instruments.Clear();
            foreach (var instrument in result.Instruments)
            {
                Vst3Instruments.Add(instrument);
            }

            _hasScannedVstInstruments = true;
            SelectedVstInstrument = Vst3Instruments.FirstOrDefault(instrument => instrument.IsPreferredDrumInstrument)
                                    ?? Vst3Instruments.FirstOrDefault();
            VstStatus = Vst3Instruments.Count == 0
                ? "No se encontraron instrumentos VST3 de 64 bits."
                : $"{Vst3Instruments.Count} instrumentos encontrados" +
                  (result.FailedModules > 0 ? $" · {result.FailedModules} módulos omitidos" : string.Empty);
        }
        catch (Exception exception)
        {
            VstStatus = $"Error al buscar VST3: {exception.Message}";
        }
        finally
        {
            IsScanningVst = false;
        }
    }

    private async Task LoadSelectedVstInstrumentAsync()
    {
        if (SelectedVstInstrument is not { } instrument)
        {
            VstStatus = "Selecciona un instrumento VST3.";
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _vstLoadCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            VstStatus = $"Cargando {instrument.DisplayName}…";
            await _audio.LoadVstInstrumentAsync(instrument, cancellation.Token);
            OnPropertyChanged(nameof(IsVstInstrumentLoaded));
            OnPropertyChanged(nameof(ActiveDrumEngineLabel));
            var editorOpened = _audio.OpenVstEditor();
            VstStatus = editorOpened
                ? $"Activo: {instrument.DisplayName} · editor abierto; elige el kit dentro del instrumento"
                : $"Activo: {instrument.DisplayName} · este instrumento no ofrece editor";
            StatusMessage = $"Los pads están conectados a {instrument.DisplayName}";
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(Volatile.Read(ref _vstLoadCancellation), cancellation))
            {
                VstStatus = "Carga del instrumento cancelada.";
            }
        }
        catch (Exception exception)
        {
            if (!ReferenceEquals(Volatile.Read(ref _vstLoadCancellation), cancellation))
            {
                return;
            }
            VstStatus = $"No se pudo cargar el instrumento: {exception.Message}";
            MessageBox.Show(
                exception.Message,
                "Instrumento VST3",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _vstLoadCancellation, null, cancellation),
                    cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private void UseInternalDrums()
    {
        var loading = Interlocked.Exchange(ref _vstLoadCancellation, null);
        loading?.Cancel();
        loading?.Dispose();
        _audio.UnloadVstInstrument();
        OnPropertyChanged(nameof(IsVstInstrumentLoaded));
        OnPropertyChanged(nameof(ActiveDrumEngineLabel));
        VstStatus = "Motor interno activo.";
        StatusMessage = "Motor de batería interno activo";
    }

    private void OpenVstEditor()
    {
        if (!_audio.IsVstInstrumentLoaded)
        {
            VstStatus = "Carga primero un instrumento VST3.";
            return;
        }

        if (!_audio.OpenVstEditor())
        {
            VstStatus = "Este instrumento no ofrece una ventana de edición.";
        }
    }

    private void OnVstInstrumentExited(object? sender, string message)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            OnPropertyChanged(nameof(IsVstInstrumentLoaded));
            OnPropertyChanged(nameof(ActiveDrumEngineLabel));
            VstStatus = message;
            StatusMessage = "Motor de batería interno activo";
        });
    }

    private void Navigate(string page)
    {
        CurrentPage = page;
        OnPropertyChanged(nameof(IsPracticePage));
        OnPropertyChanged(nameof(IsLibrariesPage));
        OnPropertyChanged(nameof(IsTracksPage));
        OnPropertyChanged(nameof(IsYouTubePage));
        OnPropertyChanged(nameof(IsSettingsPage));
    }

    private void BuildMappingRows()
    {
        MappingRows.Clear();
        foreach (var mapping in _midiProfile.NoteMappings.OrderBy(mapping => mapping.Key))
        {
            MappingRows.Add(new MidiMappingRow(mapping.Key, mapping.Value));
        }
    }

    private static MidiProfile CreateGeneralDrumProfile()
    {
        var profile = new MidiProfile { Id = "general-drums", Name = "General Drums · inicial" };
        profile.NoteMappings[36] = "kick.main";
        profile.NoteMappings[38] = "snare.center";
        profile.NoteMappings[40] = "snare.center";
        profile.NoteMappings[42] = "hihat.closed";
        profile.NoteMappings[44] = "hihat.closed";
        profile.NoteMappings[46] = "hihat.open";
        profile.NoteMappings[45] = "tom.low";
        profile.NoteMappings[48] = "tom.high";
        profile.NoteMappings[49] = "crash.edge";
        profile.NoteMappings[51] = "ride.bow";
        return profile;
    }

    private void PostStatus(string message) =>
        Application.Current.Dispatcher.BeginInvoke(() => StatusMessage = message);

    private static string FormatTime(TimeSpan value) =>
        value.TotalHours >= 1d ? value.ToString(@"h\:mm\:ss") : value.ToString(@"mm\:ss");
}

public sealed record MidiMappingRow(int Note, string Articulation)
{
    public string NoteLabel => $"Nota {Note}";
    public string ArticulationLabel => Articulation.Replace('.', ' ');
}
