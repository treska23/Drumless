using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using Microsoft.Win32;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    private bool _isRecordingOutput;
    private bool _isStartingOutputRecording;
    private bool _isStoppingOutputRecording;
    private Task? _stopOutputRecordingTask;
    private string _recordingStatus = "Preparado para grabar la mezcla de salida.";
    private LocalTrack? _lastRecordingTrack;
    private bool _isYouTubeAudioActive;

    public RelayCommand StartOutputRecordingCommand { get; private set; } = null!;
    public RelayCommand StopOutputRecordingCommand { get; private set; } = null!;
    public RelayCommand PlayLastRecordingCommand { get; private set; } = null!;

    public bool IsRecordingOutput
    {
        get => _isRecordingOutput;
        private set
        {
            if (SetProperty(ref _isRecordingOutput, value))
            {
                OnPropertyChanged(nameof(CanStartOutputRecording));
                OnPropertyChanged(nameof(CanStopOutputRecording));
            }
        }
    }

    public bool CanStartOutputRecording =>
        !IsRecordingOutput && !_isStartingOutputRecording &&
        _currentYouTubeItem is null && !_isYouTubeAudioActive;
    public bool CanStopOutputRecording => IsRecordingOutput && !_isStoppingOutputRecording;
    public bool HasLastRecording => LastRecordingTrack is not null;

    public string RecordingStatus
    {
        get => _recordingStatus;
        private set => SetProperty(ref _recordingStatus, value);
    }

    public LocalTrack? LastRecordingTrack
    {
        get => _lastRecordingTrack;
        private set
        {
            if (SetProperty(ref _lastRecordingTrack, value))
            {
                OnPropertyChanged(nameof(HasLastRecording));
            }
        }
    }

    private void InitializeRecordingCommands()
    {
        StartOutputRecordingCommand = new RelayCommand(() => _ = StartOutputRecordingAsync());
        StopOutputRecordingCommand = new RelayCommand(() => _ = StopOutputRecordingAsync());
        PlayLastRecordingCommand = new RelayCommand(() =>
        {
            if (LastRecordingTrack is not null)
            {
                _ = LoadAndSelectTrackAsync(
                    LastRecordingTrack,
                    autoPlay: true,
                    resetNavigation: true);
            }
        });
    }

    public Task CompleteRecordingBeforeCloseAsync() => StopOutputRecordingAsync();

    public void SetYouTubeAudioActive(bool active)
    {
        if (_isYouTubeAudioActive == active)
        {
            return;
        }
        _isYouTubeAudioActive = active;
        OnPropertyChanged(nameof(CanStartOutputRecording));
        if (active)
        {
            RecordingStatus = "YouTube está reproduciendo fuera del mezclador local; " +
                              "pausa el vídeo para grabar una toma local completa.";
        }
        else if (!IsRecordingOutput)
        {
            RecordingStatus = LastRecordingTrack is null
                ? "Preparado para grabar la mezcla de salida."
                : $"Última toma: {LastRecordingTrack.Title}";
        }
    }

    private async Task StartOutputRecordingAsync()
    {
        if (IsRecordingOutput)
        {
            return;
        }
        if (_currentYouTubeItem is not null)
        {
            RecordingStatus = "La grabación directa está disponible para pistas locales. " +
                              "El audio de YouTube lo reproduce el navegador y no entra en el mezclador ASIO.";
            return;
        }

        try
        {
            _isStartingOutputRecording = true;
            OnPropertyChanged(nameof(CanStartOutputRecording));
            var recordingsFolder = Path.Combine(OutputFolderPath, "Tomas");
            Directory.CreateDirectory(recordingsFolder);
            var defaultName = $"Toma - {CurrentTrack?.Title ?? "práctica"} - {DateTime.Now:yyyyMMdd-HHmmss}.wav";
            var dialog = new SaveFileDialog
            {
                Title = "Guardar grabación de la mezcla",
                Filter = "Audio WAV (*.wav)|*.wav",
                InitialDirectory = recordingsFolder,
                FileName = defaultName,
                AddExtension = true,
                DefaultExt = ".wav",
                OverwritePrompt = true
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var destination = CreateUniqueRecordingPath(dialog.FileName);
            if (Tracks.Any(track => string.Equals(
                    track.Path,
                    destination,
                    StringComparison.OrdinalIgnoreCase)))
            {
                RecordingStatus = "No se puede usar como destino un archivo ya registrado en la biblioteca.";
                return;
            }
            await _audio.StartRecordingAsync(destination);
            IsRecordingOutput = true;
            RecordingStatus = "● Grabando mezcla final: pista + instrumentos + entradas monitorizadas.";
            StatusMessage = "Grabación de salida iniciada";
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            TimeoutException)
        {
            RecordingStatus = $"No se pudo iniciar la grabación: {exception.Message}";
        }
        finally
        {
            _isStartingOutputRecording = false;
            OnPropertyChanged(nameof(CanStartOutputRecording));
        }
    }

    private Task StopOutputRecordingAsync()
    {
        if (!IsRecordingOutput)
        {
            return Task.CompletedTask;
        }
        if (_stopOutputRecordingTask is not null)
        {
            return _stopOutputRecordingTask;
        }
        _stopOutputRecordingTask = StopOutputRecordingCoreAsync();
        return _stopOutputRecordingTask;
    }

    private async Task StopOutputRecordingCoreAsync()
    {
        await Task.Yield();
        try
        {
            if (_isStoppingOutputRecording)
            {
                return;
            }
            _isStoppingOutputRecording = true;
            OnPropertyChanged(nameof(CanStopOutputRecording));
            var path = await _audio.StopRecordingAsync();
            IsRecordingOutput = false;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                RecordingStatus = "La grabación terminó sin producir un archivo.";
                return;
            }

            LastRecordingTrack = _trackLibrary.RegisterRecording(path);
            SelectedLibraryTrack = LastRecordingTrack;
            RefreshLibraryPresentation();
            SaveTrackWorkspace();
            RecordingStatus = $"Toma guardada y añadida a la biblioteca: {LastRecordingTrack.Title}" +
                              (string.IsNullOrWhiteSpace(_audio.LastRecordingWarning)
                                  ? string.Empty
                                  : $" · {_audio.LastRecordingWarning}");
            StatusMessage = "Grabación finalizada";
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            InvalidDataException or
            TimeoutException)
        {
            IsRecordingOutput = false;
            RecordingStatus = $"La grabación no pudo cerrarse correctamente: {exception.Message}";
        }
        finally
        {
            _isStoppingOutputRecording = false;
            _stopOutputRecordingTask = null;
            OnPropertyChanged(nameof(CanStopOutputRecording));
        }
    }

    private static string CreateUniqueRecordingPath(string requestedPath)
    {
        var resolved = Path.GetFullPath(requestedPath);
        if (!File.Exists(resolved))
        {
            return resolved;
        }

        var directory = Path.GetDirectoryName(resolved)!;
        var name = Path.GetFileNameWithoutExtension(resolved);
        var extension = Path.GetExtension(resolved);
        var suffix = 2;
        string candidate;
        do
        {
            candidate = Path.Combine(directory, $"{name}-{suffix++}{extension}");
        }
        while (File.Exists(candidate));
        return candidate;
    }

    private void FinalizeRecordingOnShutdown()
    {
        if (_stopOutputRecordingTask is not null)
        {
            return;
        }
        if (!IsRecordingOutput)
        {
            return;
        }
        try
        {
            var path = _audio.StopRecordingAsync().GetAwaiter().GetResult();
            IsRecordingOutput = false;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                LastRecordingTrack = _trackLibrary.RegisterRecording(path);
            }
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            InvalidDataException or
            TimeoutException)
        {
            IsRecordingOutput = false;
        }
    }
}
