using System.Diagnostics;
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
    private Task? _youtubeRecordingCaptureStartTask;
    private long _outputRecordingStartTimestamp;
    private bool _youtubeWasActiveDuringRecording;

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
        !IsRecordingOutput && !_isStartingOutputRecording;
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
        StopOutputRecordingCommand = new RelayCommand(() => _ = StopOutputRecordingAsync(force: true));
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

    public Task CompleteRecordingBeforeCloseAsync() => StopOutputRecordingAsync(force: true);

    public bool IsYouTubeAudioRouted => _isYouTubeAudioRouted;

    public void SetYouTubeBrowserProcessId(uint? processId)
    {
        _youtubeBrowserProcessId = processId is > 0 ? processId : null;
        if (IsRecordingOutput && !_isStoppingOutputRecording && _youtubeBrowserProcessId is not null)
        {
            _ = EnsureYouTubeRecordingCaptureAsync();
        }
    }

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

        if (!string.IsNullOrWhiteSpace(reason) && !IsRecordingOutput)
        {
            RecordingStatus = reason;
        }
    }

    public void SetYouTubeAudioActive(bool active)
    {
        if (_isYouTubeAudioActive == active)
        {
            if (active && IsRecordingOutput)
            {
                _youtubeWasActiveDuringRecording = true;
                _ = EnsureYouTubeRecordingCaptureAsync();
            }
            return;
        }

        _isYouTubeAudioActive = active;
        OnPropertyChanged(nameof(CanStartOutputRecording));
        if (active)
        {
            if (IsRecordingOutput)
            {
                _youtubeWasActiveDuringRecording = true;
                _ = EnsureYouTubeRecordingCaptureAsync();
                RecordingStatus = "● Grabando mezcla final: YouTube + instrumentos + entradas monitorizadas.";
            }
            else
            {
                RecordingStatus = _isYouTubeAudioRouted
                    ? "YouTube está preparado para la salida elegida y se incluirá en la grabación."
                    : "Conectando YouTube con la salida de audio elegida…";
            }
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

            await _audio.StartRecordingAsync(destination);
            startedPath = destination;
            _outputRecordingStartTimestamp = Stopwatch.GetTimestamp();
            _youtubeWasActiveDuringRecording = _isYouTubeAudioActive;
            IsRecordingOutput = true;

            // La captura de WebView2 se arma aunque YouTube todavía no esté sonando. Así la toma
            // puede empezar primero y el vídeo incorporarse después sin cortar ni desalinear nada.
            await EnsureYouTubeRecordingCaptureAsync();

            RecordingStatus = _isYouTubeAudioActive
                ? "● Grabando mezcla final: YouTube + instrumentos + entradas monitorizadas."
                : "● Grabando salida final: instrumentos, entradas y cualquier YouTube que empiece durante la toma.";
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
                try
                {
                    var abandoned = await _audio.StopRecordingAsync();
                    TryDeleteTemporaryRecording(abandoned);
                }
                catch
                {
                    TryDeleteTemporaryRecording(startedPath);
                }
            }
            IsRecordingOutput = false;
            _outputRecordingStartTimestamp = 0;
            RecordingStatus = $"No se pudo iniciar la grabación: {exception.Message}";
        }
        finally
        {
            _isStartingOutputRecording = false;
            OnPropertyChanged(nameof(CanStartOutputRecording));
        }
    }

    private Task EnsureYouTubeRecordingCaptureAsync()
    {
        if (!IsRecordingOutput ||
            _isStoppingOutputRecording ||
            _youtubeRecordingCapture is not null ||
            _youtubeBrowserProcessId is not { } browserProcessId ||
            browserProcessId == 0 ||
            _outputRecordingStartTimestamp == 0)
        {
            return Task.CompletedTask;
        }

        return _youtubeRecordingCaptureStartTask ??=
            StartYouTubeRecordingCaptureCoreAsync(
                browserProcessId,
                _outputRecordingStartTimestamp);
    }

    private async Task StartYouTubeRecordingCaptureCoreAsync(
        uint browserProcessId,
        long timelineStartTimestamp)
    {
        ProcessLoopbackWaveRecorder? preparedCapture = null;
        string? preparedPath = null;
        try
        {
            Directory.CreateDirectory(AppPaths.RecordingWork);
            preparedPath = Path.Combine(
                AppPaths.RecordingWork,
                $"youtube-{Guid.NewGuid():N}.wav");
            preparedCapture = await ProcessLoopbackWaveRecorder.PrepareAsync(
                browserProcessId,
                preparedPath);

            if (!IsRecordingOutput ||
                _isStoppingOutputRecording ||
                _youtubeRecordingCapture is not null)
            {
                return;
            }

            preparedCapture.Start(timelineStartTimestamp);
            _youtubeRecordingCapture = preparedCapture;
            _youtubeRecordingTempPath = preparedPath;
            preparedCapture = null;
            preparedPath = null;
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            TimeoutException)
        {
            if (IsRecordingOutput && _youtubeWasActiveDuringRecording)
            {
                RecordingStatus =
                    $"● La toma sigue grabando, pero YouTube aún no pudo engancharse: {exception.Message}";
            }
        }
        finally
        {
            preparedCapture?.Dispose();
            TryDeleteTemporaryRecording(preparedPath);
            _youtubeRecordingCaptureStartTask = null;
        }
    }

    private Task StopOutputRecordingAsync(bool force = false)
    {
        if (!IsRecordingOutput)
        {
            return Task.CompletedTask;
        }

        // PlayNavigationTargetAsync tenía una orden histórica de cerrar la grabación justo antes
        // de entrar en un elemento YouTube de una playlist. Durante esa transición el navegador ya
        // apunta al nuevo ID, pero _currentYouTubeItem todavía no se ha actualizado. Esa llamada no
        // debe terminar la toma: el usuario decide cuándo termina con el botón "Terminar".
        if (!force && IsQueuedYouTubeNavigationPending())
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

    private bool IsQueuedYouTubeNavigationPending()
    {
        var navigationId = _playbackNavigator.CurrentTrackId;
        if (!_playlistQueueActive ||
            string.IsNullOrWhiteSpace(navigationId) ||
            !_playlistPlaybackItems.TryGetValue(navigationId, out var target) ||
            target.Kind != PlaylistItemKind.YouTube)
        {
            return false;
        }

        return !string.Equals(
            _currentYouTubeItem?.Id,
            target.Id,
            StringComparison.Ordinal);
    }

    private async Task StopOutputRecordingCoreAsync()
    {
        await Task.Yield();
        string? mixedTempPath = null;
        ProcessLoopbackWaveRecorder? youtubeCapture = null;
        string? youtubePath = null;
        var youtubeWasActive = _youtubeWasActiveDuringRecording;
        try
        {
            if (_isStoppingOutputRecording)
            {
                return;
            }
            _isStoppingOutputRecording = true;
            OnPropertyChanged(nameof(CanStopOutputRecording));

            // Si WebView2 apareció mientras la toma ya estaba en marcha, dejamos que termine de
            // armarse o que se cancele al ver _isStoppingOutputRecording antes de tomar la referencia.
            if (_youtubeRecordingCaptureStartTask is { } pendingCaptureStart)
            {
                await pendingCaptureStart;
            }

            youtubeCapture = _youtubeRecordingCapture;
            youtubePath = _youtubeRecordingTempPath;
            _youtubeRecordingCapture = null;
            _youtubeRecordingTempPath = null;

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

                if (youtubeCapture.HasCapturedAudio &&
                    !string.IsNullOrWhiteSpace(youtubePath) &&
                    File.Exists(youtubePath))
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
                else if (youtubeWasActive)
                {
                    youtubeWarning = string.IsNullOrWhiteSpace(youtubeWarning)
                        ? "YouTube sonó durante la toma, pero no se recibió audio utilizable para el archivo."
                        : youtubeWarning + " No se recibió audio utilizable de YouTube.";
                }
            }
            else if (youtubeWasActive)
            {
                youtubeWarning =
                    "YouTube sonó durante la toma, pero WebView2 no pudo abrir su captura para el archivo.";
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
            _youtubeWasActiveDuringRecording = false;
            _outputRecordingStartTimestamp = 0;
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
            _youtubeWasActiveDuringRecording = false;
            _outputRecordingStartTimestamp = 0;
        }
    }
}
