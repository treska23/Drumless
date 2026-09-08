using DrumPracticeStudio.Audio;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
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
    private bool _isYouTubeAudioRouted;
    private uint? _youtubeBrowserProcessId;
    private ProcessLoopbackWaveRecorder? _youtubeRecordingCapture;
    private string? _youtubeRecordingTempPath;

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
        (_currentYouTubeItem is null || _isYouTubeAudioRouted);
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

    public bool IsYouTubeAudioRouted => _isYouTubeAudioRouted;

    public void SetYouTubeBrowserProcessId(uint? processId) =>
        _youtubeBrowserProcessId = processId is > 0 ? processId : null;

    public Task StartYouTubeAudioRoutingAsync(uint browserProcessId) =>
        // La creación de la captura termina añadiendo una fuente al mezclador. Si se ejecuta desde
        // una continuación del Dispatcher, un driver de audio lento puede bloquear toda la ventana.
        Task.Run(() => _audio.StartYouTubeAudioCaptureAsync(browserProcessId));

    public float TakeYouTubeAudioPeak() =>
        _audio.TakeYouTubeAudioPeak();

    public void ConfirmYouTubeAudioRouting()
    {
        if (_isYouTubeAudioRouted)
        {
            return;
        }

        _isYouTubeAudioRouted = true;
        OnPropertyChanged(nameof(IsYouTubeAudioRouted));
        OnPropertyChanged(nameof(CanStartOutputRecording));
        RecordingStatus = _isYouTubeAudioActive
            ? "YouTube está preparado para la salida elegida y se incluirá en la grabación."
            : "YouTube preparado para la salida elegida.";
    }

    public void StopYouTubeAudioRouting(string? reason = null)
    {
        // Retirar la fuente puede esperar al callback del driver o al proceso de captura. Esa espera
        // nunca debe inmovilizar el Dispatcher de WPF.
        _ = Task.Run(_audio.StopYouTubeAudioCapture);
        if (_isYouTubeAudioRouted)
        {
            _isYouTubeAudioRouted = false;
            OnPropertyChanged(nameof(IsYouTubeAudioRouted));
            OnPropertyChanged(nameof(CanStartOutputRecording));
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            RecordingStatus = reason;
        }
    }

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
            RecordingStatus = _isYouTubeAudioRouted
                ? "YouTube está preparado para la salida elegida y se incluirá en la grabación."
                : "Conectando YouTube con la salida de audio elegida…";
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
        if (_currentYouTubeItem is not null && !_isYouTubeAudioRouted)
        {
            RecordingStatus = "YouTube todavía no está preparado para la salida elegida.";
            return;
        }

        ProcessLoopbackWaveRecorder? preparedYouTubeCapture = null;
        string? preparedYouTubePath = null;
        string? startedPath = null;
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

            if (_currentYouTubeItem is not null)
            {
                if (_youtubeBrowserProcessId is not { } browserProcessId || browserProcessId == 0)
                {
                    RecordingStatus =
                        "No se puede iniciar una toma completa: WebView2 no ha expuesto el proceso de YouTube.";
                    return;
                }

                Directory.CreateDirectory(AppPaths.RecordingWork);
                preparedYouTubePath = Path.Combine(
                    AppPaths.RecordingWork,
                    $"youtube-{Guid.NewGuid():N}.wav");
                RecordingStatus = "Preparando la captura de YouTube para la mezcla final…";
                preparedYouTubeCapture = await ProcessLoopbackWaveRecorder.PrepareAsync(
                    browserProcessId,
                    preparedYouTubePath);
            }

            await _audio.StartRecordingAsync(destination);
            startedPath = destination;
            try
            {
                preparedYouTubeCapture?.Start();
            }
            catch
            {
                var abandoned = await _audio.StopRecordingAsync();
                TryDeleteTemporaryRecording(abandoned);
                throw;
            }

            _youtubeRecordingCapture = preparedYouTubeCapture;
            _youtubeRecordingTempPath = preparedYouTubePath;
            preparedYouTubeCapture = null;
            preparedYouTubePath = null;
            IsRecordingOutput = true;
            RecordingStatus = _currentYouTubeItem is not null
                ? "● Grabando mezcla final: YouTube + instrumentos + entradas monitorizadas."
                : "● Grabando mezcla final: pista + instrumentos + entradas monitorizadas.";
            StatusMessage = "Grabación de salida iniciada";
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            TimeoutException)
        {
            if (startedPath is not null)
            {
                TryDeleteTemporaryRecording(startedPath);
            }
            RecordingStatus = $"No se pudo iniciar la grabación: {exception.Message}";
        }
        finally
        {
            preparedYouTubeCapture?.Dispose();
            TryDeleteTemporaryRecording(preparedYouTubePath);
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
        var youtubeCapture = _youtubeRecordingCapture;
        var youtubePath = _youtubeRecordingTempPath;
        _youtubeRecordingCapture = null;
        _youtubeRecordingTempPath = null;
        string? mixedTempPath = null;
        try
        {
            if (_isStoppingOutputRecording)
            {
                return;
            }
            _isStoppingOutputRecording = true;
            OnPropertyChanged(nameof(CanStopOutputRecording));

            // Iniciamos el cierre de la mezcla principal y detenemos la captura paralela de
            // YouTube casi en el mismo instante. La escritura de la mezcla principal puede tardar
            // en vaciar su cola, pero ya no seguirá admitiendo audio nuevo.
            var mainStopTask = _audio.StopRecordingAsync();
            youtubeCapture?.Stop();
            var path = await mainStopTask;
            IsRecordingOutput = false;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                RecordingStatus = "La grabación terminó sin producir un archivo.";
                return;
            }

            string? youtubeWarning = null;
            if (youtubeCapture is not null)
            {
                if (youtubeCapture.Failure is { } captureFailure)
                {
                    youtubeWarning =
                        $"La captura de YouTube tuvo un problema ({captureFailure.Message}).";
                }

                if (!youtubeCapture.HasCapturedAudio ||
                    string.IsNullOrWhiteSpace(youtubePath) ||
                    !File.Exists(youtubePath))
                {
                    youtubeWarning = string.IsNullOrWhiteSpace(youtubeWarning)
                        ? "No se recibió audio de YouTube durante la toma."
                        : youtubeWarning + " No se recibió audio utilizable de YouTube.";
                }
                else
                {
                    try
                    {
                        Directory.CreateDirectory(AppPaths.RecordingWork);
                        mixedTempPath = Path.Combine(
                            AppPaths.RecordingWork,
                            $"final-{Guid.NewGuid():N}.wav");
                        await AudioFileMixService.MixAsync(
                            [path, youtubePath],
                            mixedTempPath);
                        File.Copy(mixedTempPath, path, overwrite: true);
                    }
                    catch (Exception exception) when (exception is
                        IOException or
                        InvalidDataException or
                        InvalidOperationException or
                        UnauthorizedAccessException)
                    {
                        youtubeWarning =
                            $"YouTube no pudo incorporarse a la toma ({exception.Message}).";
                    }
                }
            }

            LastRecordingTrack = _trackLibrary.RegisterRecording(path);
            SelectedLibraryTrack = LastRecordingTrack;
            RefreshLibraryPresentation();
            SaveTrackWorkspace();
            var warnings = new[]
                {
                    _audio.LastRecordingWarning,
                    youtubeWarning
                }
                .Where(warning => !string.IsNullOrWhiteSpace(warning))
                .ToArray();
            RecordingStatus = $"Toma guardada y añadida a la biblioteca: {LastRecordingTrack.Title}" +
                              (warnings.Length == 0
                                  ? string.Empty
                                  : $" · {string.Join(" · ", warnings)}");
            StatusMessage = warnings.Length == 0
                ? "Grabación finalizada"
                : "Grabación finalizada con avisos";
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
            youtubeCapture?.Dispose();
            TryDeleteTemporaryRecording(youtubePath);
            TryDeleteTemporaryRecording(mixedTempPath);
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

    private static void TryDeleteTemporaryRecording(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
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
            _youtubeRecordingCapture?.Stop();
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
        finally
        {
            _youtubeRecordingCapture?.Dispose();
            _youtubeRecordingCapture = null;
            TryDeleteTemporaryRecording(_youtubeRecordingTempPath);
            _youtubeRecordingTempPath = null;
        }
    }
}
