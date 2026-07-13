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
    private readonly Vst3PresetDiscoveryService _vstPresetDiscovery = new();
    private readonly AudioOutputDeviceService _audioOutputDevices = new();
    private readonly DrumRemovalService _drumRemoval = new();
    private readonly AudioEngine _audio = new();
    private readonly MidiInputService _midi = new();
    private readonly MidiProfile _midiProfile;
    private readonly DispatcherTimer _transportTimer;
    private readonly DispatcherTimer _settingsSaveTimer;
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
    private AudioInputChannelItem? _selectedAudioInputChannel;
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
    private Vst3ProgramItem? _selectedVstProgram;
    private string _vstStatus = "Pulsa «Buscar instrumentos» para localizar Addictive Drums y Groove Agent de forma aislada.";
    private bool _isScanningVst;
    private string _audioOutputStatus = "Buscando salidas de audio…";
    private string _audioInputStatus = "Selecciona una salida ASIO para usar la entrada de audio.";
    private double _audioInputGain = 0.8d;
    private double _midiVelocitySensitivity = MidiVelocityCurve.DefaultSensitivity;
    private string _midiVelocityMonitor = "Toca un pad para comprobar la velocidad recibida.";
    private string? _preferredAudioOutputDeviceId;
    private string? _preferredAudioInputOutputDeviceId;
    private int? _preferredAudioInputChannelIndex;
    private string? _preferredMidiDeviceName;
    private int? _preferredMidiDeviceIndex;
    private bool _autoConnectMidi = true;
    private string? _preferredInternalLibraryId;
    private string? _preferredInternalKitId;
    private string? _preferredVstModulePath;
    private string? _preferredVstClassId;
    private bool _autoLoadVst;
    private string? _activeVstClassId;
    private bool _hasInitializedAudioDevices;
    private bool _hasInitializedMidiDevices;
    private bool _isRefreshingMidiDevices;
    private bool _isRefreshingAudioDevices;
    private bool _isRefreshingAudioInputChannels;
    private bool _isSynchronizingVstInstrumentSelection;
    private bool _isRefreshingVstPrograms;

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
        AudioInputChannels = [];
        MappingRows = [];
        Vst3Instruments = [];
        Vst3Programs = [];
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
        _audio.SetTrackVolume((float)_trackVolume);

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
        RefreshAudioOutputsCommand = new RelayCommand(RefreshAudioOutputDevices);
        ScanVstInstrumentsCommand = new RelayCommand(() => _ = ScanVstInstrumentsAsync(force: true));
        LoadVstPresetCommand = new RelayCommand(LoadVstPreset);
        UseInternalDrumsCommand = new RelayCommand(UseInternalDrums);
        OpenVstEditorCommand = new RelayCommand(OpenVstEditor);
        PanicVstCommand = new RelayCommand(() => _audio.PanicVstInstrument());
        InitializeTrackCommands();

        _midi.NoteReceived += OnMidiNoteReceived;
        _midi.NoteOffReceived += OnMidiNoteOffReceived;
        _midi.ControlChangeReceived += OnMidiControlChangeReceived;
        _midi.Error += (_, message) => PostStatus($"MIDI: {message}");
        _audio.VstInstrumentExited += OnVstInstrumentExited;
        _audio.VstEditorClosed += OnVstEditorClosed;

        _transportTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _transportTimer.Tick += (_, _) => RefreshTransport();
        _transportTimer.Start();

        _settingsSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SaveTrackWorkspace(silent: true);
        };

        RefreshMidiDevices();
        RefreshAudioOutputDevices();
        RestoreInternalKitConfiguration();
        _ = RestoreVstConfigurationAsync();

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
    public ObservableCollection<AudioInputChannelItem> AudioInputChannels { get; }
    public ObservableCollection<MidiMappingRow> MappingRows { get; }
    public ObservableCollection<Vst3InstrumentItem> Vst3Instruments { get; }
    public ObservableCollection<Vst3ProgramItem> Vst3Programs { get; }
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
    public RelayCommand RefreshAudioOutputsCommand { get; }
    public RelayCommand ScanVstInstrumentsCommand { get; }
    public RelayCommand LoadVstPresetCommand { get; }
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
        set
        {
            if (!SetProperty(ref _selectedMidiDevice, value) || value is null || _isRefreshingMidiDevices)
            {
                return;
            }

            RememberMidiDevice(value);
            ScheduleSettingsSave();
            ConnectSelectedMidiDevice();
        }
    }

    public AudioOutputDeviceItem? SelectedAudioOutputDevice
    {
        get => _selectedAudioOutputDevice;
        set
        {
            if (!SetProperty(ref _selectedAudioOutputDevice, value) ||
                value is null ||
                _isRefreshingAudioDevices)
            {
                return;
            }

            _ = ApplyAudioOutputSelectionAsync(value, showDialog: true, remember: true);
        }
    }

    public string AudioOutputStatus
    {
        get => _audioOutputStatus;
        private set => SetProperty(ref _audioOutputStatus, value);
    }

    public AudioInputChannelItem? SelectedAudioInputChannel
    {
        get => _selectedAudioInputChannel;
        set
        {
            if (!SetProperty(ref _selectedAudioInputChannel, value) ||
                value is null ||
                _isRefreshingAudioInputChannels)
            {
                return;
            }

            ApplyAudioInputSelection(value);
        }
    }

    public string AudioInputStatus
    {
        get => _audioInputStatus;
        private set => SetProperty(ref _audioInputStatus, value);
    }

    public double AudioInputGain
    {
        get => _audioInputGain;
        set
        {
            var bounded = Math.Clamp(value, 0d, 1.5d);
            if (!SetProperty(ref _audioInputGain, bounded))
            {
                return;
            }

            _audio.SetAudioInputGain((float)bounded);
            OnPropertyChanged(nameof(AudioInputGainLabel));
            ScheduleSettingsSave();
        }
    }

    public string AudioInputGainLabel => $"{AudioInputGain * 100:0}%";

    public bool IsAudioInputAvailable =>
        SelectedAudioOutputDevice?.IsAsio == true && AudioInputChannels.Count > 1;

    public double MidiVelocitySensitivity
    {
        get => _midiVelocitySensitivity;
        set
        {
            var bounded = Math.Clamp(value, 0d, 100d);
            if (SetProperty(ref _midiVelocitySensitivity, bounded))
            {
                OnPropertyChanged(nameof(MidiVelocitySensitivityLabel));
                ScheduleSettingsSave();
            }
        }
    }

    public string MidiVelocitySensitivityLabel => MidiVelocitySensitivity switch
    {
        < 35d => $"{MidiVelocitySensitivity:0} · menos sensible",
        < 60d => $"{MidiVelocitySensitivity:0} · lineal",
        < 85d => $"{MidiVelocitySensitivity:0} · sensible",
        _ => $"{MidiVelocitySensitivity:0} · muy sensible"
    };

    public string MidiVelocityMonitor
    {
        get => _midiVelocityMonitor;
        private set => SetProperty(ref _midiVelocityMonitor, value);
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
        private set => SetProperty(ref _midiStatus, value);
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
                ScheduleSettingsSave();
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
        set
        {
            if (!SetProperty(ref _selectedVstInstrument, value) ||
                value is null ||
                _isSynchronizingVstInstrumentSelection)
            {
                return;
            }

            _ = LoadSelectedVstInstrumentAsync();
        }
    }

    public Vst3ProgramItem? SelectedVstProgram
    {
        get => _selectedVstProgram;
        set
        {
            if (!SetProperty(ref _selectedVstProgram, value) ||
                value is null ||
                _isRefreshingVstPrograms)
            {
                return;
            }

            SelectVstProgram();
        }
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
    public string VstAudioStatus => _audio.VstAudioStatus;
    public bool HasVstPrograms => Vst3Programs.Count > 0;
    public string ActiveDrumEngineLabel => _audio.IsVstInstrumentLoaded
        ? _audio.IsDirectVstInstrumentLoaded
            ? $"VST3 directo · {_audio.VstInstrumentName}"
            : $"VST3 aislado · {_audio.VstInstrumentName}"
        : _audio.IsExternalInstrumentSelected
            ? $"VST3 detenido · {_audio.VstInstrumentName ?? "sin instrumento"} · kit interno bloqueado"
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
    public void Dispose()
    {
        _transportTimer.Stop();
        _settingsSaveTimer.Stop();
        _kitLoadCancellation?.Cancel();
        _kitLoadCancellation?.Dispose();
        _drumRemovalCancellation?.Cancel();
        _drumRemovalCancellation?.Dispose();
        _vstLoadCancellation?.Cancel();
        _vstLoadCancellation?.Dispose();
        _trackLoadCancellation?.Cancel();
        _trackLoadCancellation?.Dispose();
        SaveActiveVstState(silent: true);
        SaveTrackWorkspace(silent: true);
        _audio.VstInstrumentExited -= OnVstInstrumentExited;
        _audio.VstEditorClosed -= OnVstEditorClosed;
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
            _preferredInternalLibraryId = kit.LibraryId;
            _preferredInternalKitId = kit.Id;
            ScheduleSettingsSave();
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
        var initialSetup = !_hasInitializedMidiDevices;
        var rememberedName = initialSetup
            ? _preferredMidiDeviceName
            : SelectedMidiDevice?.Name ?? _preferredMidiDeviceName;
        var rememberedIndex = initialSetup
            ? _preferredMidiDeviceIndex
            : SelectedMidiDevice?.Index ?? _preferredMidiDeviceIndex;

        _isRefreshingMidiDevices = true;
        try
        {
            MidiDevices.Clear();
            foreach (var device in _midi.GetDevices())
            {
                MidiDevices.Add(device);
            }

            SelectedMidiDevice = DeviceAutoConfiguration.SelectMidiInput(
                MidiDevices,
                rememberedName,
                rememberedIndex);
        }
        finally
        {
            _isRefreshingMidiDevices = false;
        }

        _hasInitializedMidiDevices = true;
        if (MidiDevices.Count == 0)
        {
            MidiStatus = "No se encontraron entradas MIDI";
            return;
        }

        if (SelectedMidiDevice is not null)
        {
            ConnectSelectedMidiDevice();
        }
    }

    private void RefreshAudioOutputDevices()
    {
        try
        {
            var initialSetup = !_hasInitializedAudioDevices;
            var selectedId = initialSetup
                ? _preferredAudioOutputDeviceId
                : SelectedAudioOutputDevice?.Id ?? _preferredAudioOutputDeviceId;
            _isRefreshingAudioDevices = true;
            try
            {
                AudioOutputDevices.Clear();
                foreach (var device in _audioOutputDevices.GetDevices())
                {
                    AudioOutputDevices.Add(device);
                }

                SelectedAudioOutputDevice = DeviceAutoConfiguration.SelectAudioOutput(
                    AudioOutputDevices,
                    selectedId);
            }
            finally
            {
                _isRefreshingAudioDevices = false;
            }

            _hasInitializedAudioDevices = true;
            AudioOutputStatus = AudioOutputDevices.Count == 0
                ? "No se encontraron salidas de audio activas."
                : _audio.Status;

            if (SelectedAudioOutputDevice is { } selected &&
                (initialSetup ||
                 !string.Equals(selected.Id, _audio.OutputDeviceId, StringComparison.Ordinal)))
            {
                var applied = TryApplyAudioOutputDevice(selected, showDialog: false, remember: false);
                if (applied && string.IsNullOrWhiteSpace(_preferredAudioOutputDeviceId))
                {
                    _preferredAudioOutputDeviceId = selected.Id;
                    ScheduleSettingsSave();
                }
                else if (!applied && selected.IsAsio)
                {
                    var fallback = AudioOutputDevices.FirstOrDefault(device => device.IsDefault) ??
                                   AudioOutputDevices.FirstOrDefault(device => !device.IsAsio);
                    if (fallback is not null)
                    {
                        _isRefreshingAudioDevices = true;
                        try
                        {
                            SelectedAudioOutputDevice = fallback;
                        }
                        finally
                        {
                            _isRefreshingAudioDevices = false;
                        }
                        TryApplyAudioOutputDevice(fallback, showDialog: false, remember: false);
                        AudioOutputStatus += $" · {selected.Name} no estaba disponible; se usó WASAPI temporalmente";
                    }
                }
            }
        }
        catch (Exception exception)
        {
            AudioOutputStatus = $"No se pudieron leer las salidas: {exception.Message}";
        }
    }

    private bool TryApplyAudioOutputDevice(
        AudioOutputDeviceItem selected,
        bool showDialog,
        bool remember)
    {
        try
        {
            var rememberedInput = selected.IsAsio &&
                                  string.Equals(
                                      selected.Id,
                                      _preferredAudioInputOutputDeviceId,
                                      StringComparison.Ordinal)
                ? _preferredAudioInputChannelIndex
                : null;
            try
            {
                _audio.SelectOutputDevice(
                    selected.Id,
                    rememberedInput,
                    (float)AudioInputGain);
            }
            catch when (rememberedInput is not null)
            {
                _audio.SelectOutputDevice(
                    selected.Id,
                    inputChannelIndex: null,
                    inputGain: (float)AudioInputGain);
                _preferredAudioInputChannelIndex = null;
                AudioInputStatus = "La entrada guardada ya no existe; la monitorización quedó desactivada.";
            }
            RefreshAudioInputChannels();
            AudioOutputStatus = _audio.Status;
            StatusMessage = $"El programa y el VST3 suenan por {selected.Name}";
            if (_audio.IsVstInstrumentLoaded)
            {
                VstStatus = $"Activo: {_audio.VstInstrumentName} · salida {selected.Name}";
                OnPropertyChanged(nameof(VstAudioStatus));
                OnPropertyChanged(nameof(ActiveDrumEngineLabel));
            }
            if (remember)
            {
                _preferredAudioOutputDeviceId = selected.Id;
                ScheduleSettingsSave();
            }
            return true;
        }
        catch (Exception exception)
        {
            AudioOutputStatus = $"No se pudo usar {selected.Name}: {exception.Message}";
            if (showDialog)
            {
                MessageBox.Show(
                    $"No se pudo abrir la salida «{selected.Name}».\n\n{exception.Message}\n\n" +
                    "La salida anterior sigue activa.",
                    "Salida de audio",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            return false;
        }
    }

    private void RefreshAudioInputChannels()
    {
        _isRefreshingAudioInputChannels = true;
        try
        {
            AudioInputChannels.Clear();
            AudioInputChannels.Add(new AudioInputChannelItem(null, "Desactivada"));
            foreach (var channel in _audio.AudioInputChannels)
            {
                AudioInputChannels.Add(channel);
            }

            SelectedAudioInputChannel = AudioInputChannels.FirstOrDefault(channel =>
                                            channel.ChannelIndex == _audio.AudioInputChannelIndex)
                                        ?? AudioInputChannels[0];
        }
        finally
        {
            _isRefreshingAudioInputChannels = false;
        }

        OnPropertyChanged(nameof(IsAudioInputAvailable));
        AudioInputStatus = !_audio.AudioInputChannels.Any()
            ? "La entrada de audio directa está disponible al elegir una salida ASIO."
            : _audio.IsAudioInputMonitoringActive
                ? $"Monitorización directa activa · {_audio.Status}"
                : "Elige el jack donde has conectado la salida del módulo de batería.";
    }

    private void ApplyAudioInputSelection(AudioInputChannelItem selected)
    {
        try
        {
            _audio.SelectAudioInputChannel(selected.ChannelIndex, (float)AudioInputGain);
            _preferredAudioInputOutputDeviceId = SelectedAudioOutputDevice?.Id;
            _preferredAudioInputChannelIndex = selected.ChannelIndex;
            AudioOutputStatus = _audio.Status;
            AudioInputStatus = selected.IsDisabled
                ? "Monitorización de entrada desactivada."
                : $"Monitorizando {selected.DisplayName} por el mismo callback ASIO.";
            ScheduleSettingsSave();
            OnPropertyChanged(nameof(VstAudioStatus));
        }
        catch (Exception exception)
        {
            AudioInputStatus = $"No se pudo abrir {selected.DisplayName}: {exception.Message}";
            _isRefreshingAudioInputChannels = true;
            try
            {
                SelectedAudioInputChannel = AudioInputChannels.FirstOrDefault(channel =>
                                                channel.ChannelIndex == _audio.AudioInputChannelIndex)
                                            ?? AudioInputChannels.FirstOrDefault();
            }
            finally
            {
                _isRefreshingAudioInputChannels = false;
            }

            MessageBox.Show(
                $"No se pudo monitorizar «{selected.DisplayName}».\n\n{exception.Message}\n\n" +
                "La configuración anterior sigue activa.",
                "Entrada de audio",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async Task ApplyAudioOutputSelectionAsync(
        AudioOutputDeviceItem selected,
        bool showDialog,
        bool remember)
    {
        var reloadVst = AudioOutputTransitionPolicy.RequiresVstReload(
            selected.IsAsio,
            _audio.IsVstInstrumentLoaded,
            _audio.IsDirectVstInstrumentLoaded);
        var instrumentToReload = reloadVst
            ? SelectedVstInstrument ?? Vst3Instruments.FirstOrDefault(instrument =>
                string.Equals(
                    instrument.PluginClass.ClassId,
                    _activeVstClassId,
                    StringComparison.OrdinalIgnoreCase))
            : null;
        if (reloadVst && instrumentToReload is null)
        {
            AudioOutputStatus = "No se pudo identificar el VST3 activo para reiniciarlo con ASIO.";
            return;
        }
        if (reloadVst)
        {
            SaveActiveVstState(silent: true);
            _audio.UnloadVstInstrument();
            OnPropertyChanged(nameof(IsVstInstrumentLoaded));
            OnPropertyChanged(nameof(ActiveDrumEngineLabel));
            OnPropertyChanged(nameof(VstAudioStatus));
            VstStatus = $"Cambiando a {selected.Name} y reiniciando el instrumento…";
        }

        var applied = TryApplyAudioOutputDevice(selected, showDialog, remember);
        if (!applied)
        {
            RestoreActiveAudioOutputSelection();
        }

        if (instrumentToReload is not null)
        {
            _isSynchronizingVstInstrumentSelection = true;
            try
            {
                SelectedVstInstrument = instrumentToReload;
            }
            finally
            {
                _isSynchronizingVstInstrumentSelection = false;
            }

            await LoadSelectedVstInstrumentAsync(showMessage: showDialog);
        }
    }

    private void RestoreActiveAudioOutputSelection()
    {
        var active = AudioOutputDevices.FirstOrDefault(device =>
            string.Equals(device.Id, _audio.OutputDeviceId, StringComparison.Ordinal));
        if (active is null)
        {
            return;
        }

        _isRefreshingAudioDevices = true;
        try
        {
            SelectedAudioOutputDevice = active;
        }
        finally
        {
            _isRefreshingAudioDevices = false;
        }
    }

    private void ConnectSelectedMidiDevice()
    {
        if (SelectedMidiDevice is not { } selected)
        {
            MidiStatus = "Selecciona una entrada MIDI";
            return;
        }

        try
        {
            _midi.Connect(selected.Index);
            RememberMidiDevice(selected);
            _autoConnectMidi = true;
            MidiStatus = $"Conectado a {selected.Name}";
            StatusMessage = "MIDI conectado · toca un pad para probar el kit activo";
            ScheduleSettingsSave();
        }
        catch (Exception exception)
        {
            MidiStatus = $"Error: {exception.Message}";
        }
    }

    private void RememberMidiDevice(MidiDeviceItem device)
    {
        _preferredMidiDeviceName = device.Name;
        _preferredMidiDeviceIndex = device.Index;
    }

    private void ScheduleSettingsSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void OnMidiNoteReceived(object? sender, MidiNoteMessage message)
    {
        var hasMapping = _midiProfile.TryResolve(message.Note, out var articulation);
        if (!hasMapping && !_audio.IsExternalInstrumentSelected)
        {
            return;
        }

        var adjustedVelocity = MidiVelocityCurve.Apply(
            message.Velocity,
            Volatile.Read(ref _midiVelocitySensitivity));
        _audio.Trigger(articulation ?? string.Empty, adjustedVelocity, message.Note, message.Channel);

        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            MidiVelocityMonitor = $"Último golpe: entrada {message.Velocity} → salida {adjustedVelocity}";
            if (!hasMapping)
            {
                return;
            }

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
            _isSynchronizingVstInstrumentSelection = true;
            try
            {
                SelectedVstInstrument = Vst3Instruments.FirstOrDefault(instrument =>
                                            string.Equals(
                                                instrument.Module.Path,
                                                _preferredVstModulePath,
                                                StringComparison.OrdinalIgnoreCase) &&
                                            string.Equals(
                                                instrument.PluginClass.ClassId,
                                                _preferredVstClassId,
                                                StringComparison.OrdinalIgnoreCase))
                                        ?? Vst3Instruments.FirstOrDefault(instrument => instrument.IsPreferredDrumInstrument)
                                        ?? Vst3Instruments.FirstOrDefault();
            }
            finally
            {
                _isSynchronizingVstInstrumentSelection = false;
            }
            VstStatus = Vst3Instruments.Count == 0
                ? "No se encontraron instrumentos VST3 de 64 bits."
                : $"{Vst3Instruments.Count} instrumentos encontrados" +
                  (result.FailedModules > 0 ? $" · {result.FailedModules} módulos omitidos" : string.Empty);

            if (force && SelectedVstInstrument is not null)
            {
                await LoadSelectedVstInstrumentAsync();
            }
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

    private async Task LoadSelectedVstInstrumentAsync(bool showMessage = true)
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
            SaveActiveVstState(silent: true);
            VstStatus = $"Cargando {instrument.DisplayName}…";
            await _audio.LoadVstInstrumentAsync(instrument, cancellation.Token);
            _activeVstClassId = instrument.PluginClass.ClassId;
            _preferredVstModulePath = instrument.Module.Path;
            _preferredVstClassId = instrument.PluginClass.ClassId;
            _autoLoadVst = true;
            var restoredVstState = TryRestoreVstState(instrument);
            ScheduleSettingsSave();
            OnPropertyChanged(nameof(IsVstInstrumentLoaded));
            OnPropertyChanged(nameof(ActiveDrumEngineLabel));
            OnPropertyChanged(nameof(VstAudioStatus));
            _isRefreshingVstPrograms = true;
            try
            {
                Vst3Programs.Clear();
                for (var index = 0; index < _audio.VstPrograms.Count; index++)
                {
                    Vst3Programs.Add(new Vst3ProgramItem(index, _audio.VstPrograms[index]));
                }

                SelectedVstProgram = Vst3Programs.FirstOrDefault(program =>
                                         program.Index == _audio.CurrentVstProgram)
                                     ?? Vst3Programs.FirstOrDefault();
            }
            finally
            {
                _isRefreshingVstPrograms = false;
            }
            OnPropertyChanged(nameof(HasVstPrograms));
            VstStatus = Vst3Programs.Count > 0
                ? $"Activo: {instrument.DisplayName} · " +
                  $"{(_audio.IsDirectVstInstrumentLoaded ? "ASIO directo" : "motor aislado")} · " +
                  $"{Vst3Programs.Count} programas expuestos por VST3"
                : $"Activo: {instrument.DisplayName} · " +
                  $"{(_audio.IsDirectVstInstrumentLoaded ? "ASIO directo" : "motor aislado")} · " +
                  "no expone su catálogo de kits al host; " +
                  "puedes cargar un .vstpreset o abrir el editor avanzado";
            if (!string.IsNullOrWhiteSpace(restoredVstState))
            {
                VstStatus += $" · {restoredVstState}";
            }
            else if (instrument.DisplayName.Contains("Groove Agent", StringComparison.OrdinalIgnoreCase))
            {
                VstStatus += " · no se encontró un kit compatible guardado; " +
                             "elige uno en el editor y ciérralo para recordarlo";
            }
            AudioOutputStatus = _audio.Status;
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
            OnPropertyChanged(nameof(IsVstInstrumentLoaded));
            OnPropertyChanged(nameof(ActiveDrumEngineLabel));
            OnPropertyChanged(nameof(VstAudioStatus));
            if (showMessage)
            {
                MessageBox.Show(
                    exception.Message,
                    "Instrumento VST3",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
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
        SaveActiveVstState(silent: true);
        var loading = Interlocked.Exchange(ref _vstLoadCancellation, null);
        loading?.Cancel();
        loading?.Dispose();
        _audio.UnloadVstInstrument();
        Vst3Programs.Clear();
        SelectedVstProgram = null;
        _isSynchronizingVstInstrumentSelection = true;
        try
        {
            SelectedVstInstrument = null;
        }
        finally
        {
            _isSynchronizingVstInstrumentSelection = false;
        }
        OnPropertyChanged(nameof(HasVstPrograms));
        OnPropertyChanged(nameof(IsVstInstrumentLoaded));
        OnPropertyChanged(nameof(ActiveDrumEngineLabel));
        OnPropertyChanged(nameof(VstAudioStatus));
        _activeVstClassId = null;
        _autoLoadVst = false;
        ScheduleSettingsSave();
        VstStatus = "Motor interno activo.";
        StatusMessage = "Motor de batería interno activo";
    }

    private void SelectVstProgram()
    {
        if (!_audio.IsVstInstrumentLoaded || SelectedVstProgram is not { } program)
        {
            VstStatus = "El instrumento no ofrece un programa seleccionable.";
            return;
        }

        try
        {
            _audio.SelectVstProgram(program.Index);
            VstStatus = $"Activo: {_audio.VstInstrumentName} · kit/programa: {program.DisplayName}";
            SaveActiveVstState(silent: true);
        }
        catch (Exception exception)
        {
            VstStatus = $"No se pudo seleccionar el programa: {exception.Message}";
        }
    }

    private void LoadVstPreset()
    {
        if (!_audio.IsVstInstrumentLoaded)
        {
            VstStatus = "Carga primero Groove Agent u otro instrumento VST3.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Cargar preset del instrumento VST3",
            Filter = "Preset VST3 (*.vstpreset)|*.vstpreset",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _audio.LoadVstPreset(dialog.FileName);
            VstStatus = $"Preset enviado a {_audio.VstInstrumentName}: {Path.GetFileName(dialog.FileName)}";
            SaveActiveVstState(silent: true);
        }
        catch (Exception exception)
        {
            VstStatus = $"No se pudo cargar el preset: {exception.Message}";
        }
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
            OnPropertyChanged(nameof(VstAudioStatus));
            VstStatus = message;
            StatusMessage = "VST3 detenido · la batería está silenciada; el kit interno no se ha activado";
        });
    }

    private void OnVstEditorClosed(object? sender, EventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            SaveActiveVstState(silent: false);
            if (_audio.IsVstInstrumentLoaded)
            {
                VstStatus = $"Kit de {_audio.VstInstrumentName ?? "Groove Agent"} guardado; " +
                            "se restaurará en la próxima apertura.";
            }
        });
    }

    private async Task RestoreVstConfigurationAsync()
    {
        if (!_autoLoadVst ||
            string.IsNullOrWhiteSpace(_preferredVstModulePath) ||
            string.IsNullOrWhiteSpace(_preferredVstClassId))
        {
            return;
        }

        await ScanVstInstrumentsAsync(force: false);
        var remembered = Vst3Instruments.FirstOrDefault(instrument =>
            string.Equals(instrument.Module.Path, _preferredVstModulePath, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(instrument.PluginClass.ClassId, _preferredVstClassId, StringComparison.OrdinalIgnoreCase));
        if (remembered is null)
        {
            VstStatus = "El instrumento VST3 guardado no está disponible; usa «Buscar instrumentos».";
            return;
        }

        _isSynchronizingVstInstrumentSelection = true;
        try
        {
            SelectedVstInstrument = remembered;
        }
        finally
        {
            _isSynchronizingVstInstrumentSelection = false;
        }
        await LoadSelectedVstInstrumentAsync(showMessage: false);
    }

    private void RestoreInternalKitConfiguration()
    {
        var rememberedKit = Libraries
            .Where(library => string.IsNullOrWhiteSpace(_preferredInternalLibraryId) ||
                              string.Equals(
                                  library.Id,
                                  _preferredInternalLibraryId,
                                  StringComparison.OrdinalIgnoreCase))
            .SelectMany(library => library.Kits)
            .FirstOrDefault(kit => string.Equals(
                kit.Id,
                _preferredInternalKitId,
                StringComparison.OrdinalIgnoreCase));
        var startupKit = rememberedKit ?? Libraries.SelectMany(library => library.Kits).FirstOrDefault();
        if (startupKit is null)
        {
            return;
        }

        _isSynchronizingKitSelection = true;
        try
        {
            SelectedLibrary = Libraries.FirstOrDefault(library =>
                string.Equals(library.Id, startupKit.LibraryId, StringComparison.OrdinalIgnoreCase));
            SelectedKit = startupKit;
        }
        finally
        {
            _isSynchronizingKitSelection = false;
        }

        _ = ActivateKitAsync(startupKit);
    }

    private void SaveActiveVstState(bool silent)
    {
        if (!_audio.IsVstInstrumentLoaded || string.IsNullOrWhiteSpace(_activeVstClassId))
        {
            return;
        }

        try
        {
            _audio.SaveVstPreset(GetAutomaticVstStatePath(_activeVstClassId));
        }
        catch (Exception exception)
        {
            if (!silent)
            {
                VstStatus = $"No se pudo guardar el estado del VST3: {exception.Message}";
            }
        }
    }

    private string? TryRestoreVstState(Vst3InstrumentItem instrument)
    {
        var automaticPath = GetAutomaticVstStatePath(instrument.PluginClass.ClassId);
        if (File.Exists(automaticPath))
        {
            try
            {
                _audio.LoadVstPreset(automaticPath);
                return "kit anterior restaurado";
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                InvalidOperationException or
                NotSupportedException)
            {
                VstStatus = $"El estado anterior no era válido: {exception.Message}";
            }
        }

        var discoveredPath = _vstPresetDiscovery.FindCompatiblePreset(instrument);
        if (discoveredPath is null)
        {
            return null;
        }

        try
        {
            _audio.LoadVstPreset(discoveredPath);
            SaveActiveVstState(silent: true);
            return $"kit inicial: {Path.GetFileNameWithoutExtension(discoveredPath)}";
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            InvalidOperationException or
            NotSupportedException)
        {
            VstStatus = $"No se pudo cargar el preset inicial encontrado: {exception.Message}";
            return null;
        }
    }

    private static string GetAutomaticVstStatePath(string classId) =>
        Path.Combine(AppPaths.VstStates, $"{classId}.vstpreset");

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
