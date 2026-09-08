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
    private Task? _startOutputRecordingTask;
    private bool _isClosingForRecording;
    private string _recordingStatus = "Preparado para grabar la mezcla de salida.";
    private LocalTrack? _lastRecordingTrack;
    private bool _isYouTubeAudioActive;
    private bool _isYouTubeAudioRouted;
    private OutputEndpointCaptureSession? _outputEndpointCapture;
    private string? _recordingDestinationPath;
    private string? _recordingWorkDirectory;
    private bool _recordingUsesEndpointAsPrimary;
    private bool _recordingInternalStarted;
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
                NotifyRecordingAvailability();
            }
        }
    }

    public bool CanStartOutputRecording =>
        !HasPendingOutputRecording && !_isClosingForRecording;
    public bool HasPendingOutputRecording =>
        IsRecordingOutput || _isStartingOutputRecording || _isStoppingOutputRecording ||
        _stopOutputRecordingTask is not null;
    public bool CanChangeRecordingAudioSetup => !HasPendingOutputRecording && !_isClosingForRecording;
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

    public async Task CompleteRecordingBeforeCloseAsync()
    {
        _isClosingForRecording = true;
        NotifyRecordingAvailability();
        if (_startOutputRecordingTask is { } starting)
        {
            await starting;
        }
        await StopOutputRecordingAsync(force: true);
    }

    private void NotifyRecordingAvailability()
    {
        OnPropertyChanged(nameof(CanStartOutputRecording));
        OnPropertyChanged(nameof(CanStopOutputRecording));
        OnPropertyChanged(nameof(HasPendingOutputRecording));
        OnPropertyChanged(nameof(CanChangeRecordingAudioSetup));
    }

    public bool IsYouTubeAudioRouted => _isYouTubeAudioRouted;

    public Task StartYouTubeAudioRoutingAsync(uint browserProcessId) =>
        // Se conserva únicamente para la ruta histórica de reproducción. La grabación nueva no
        // captura WebView2 por proceso: captura el endpoint físico de Windows.
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
        if (!IsRecordingOutput)
        {
            RecordingStatus = _isYouTubeAudioActive
                ? "YouTube conectado a la salida elegida."
                : "YouTube preparado para la salida elegida.";
        }
    }

    public void StopYouTubeAudioRouting(string? reason = null)
    {
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
                RecordingStatus = "● Grabando salida física: YouTube + mezcla de Drumless.";
            }
            else
            {
                RecordingStatus = "YouTube preparado; Grabar capturará la salida física seleccionada.";
            }
        }
        else if (!IsRecordingOutput)
        {
            RecordingStatus = LastRecordingTrack is null
                ? "Preparado para grabar la mezcla de salida."
                : $"Última toma: {LastRecordingTrack.Title}";
        }
    }

    private Task StartOutputRecordingAsync()
    {
        if (!CanStartOutputRecording)
        {
            return Task.CompletedTask;
        }

        _isStartingOutputRecording = true;
        NotifyRecordingAvailability();
        _startOutputRecordingTask = StartOutputRecordingCoreAsync();
        return _startOutputRecordingTask;
    }

    private async Task StartOutputRecordingCoreAsync()
    {
        // Publish the task before any nested dialog/dispatcher can request a close.
        await Task.Yield();

        OutputEndpointCaptureSession? preparedEndpointCapture = null;
        string? workDirectory = null;
        string? destination = null;
        var internalStarted = false;
        try
        {
            if (_isClosingForRecording) return;

            var selectedOutput = SelectedAudioOutputDevice ??
                throw new InvalidOperationException("Selecciona una salida de audio antes de grabar.");

            var recordingsFolder = Path.Combine(OutputFolderPath, "Tomas");
            Directory.CreateDirectory(recordingsFolder);
            var defaultName = $"Toma - {CurrentTrack?.Title ?? "práctica"} - {DateTime.Now:yyyyMMdd-HHmmss}.wav";
            var dialog = new SaveFileDialog
            {
                Title = "Guardar grabación de la salida",
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

            destination = CreateUniqueRecordingPath(dialog.FileName);
            if (Tracks.Any(track => string.Equals(
                    track.Path,
                    destination,
                    StringComparison.OrdinalIgnoreCase)))
            {
                RecordingStatus = "No se puede usar como destino un archivo ya registrado en la biblioteca.";
                return;
            }

            var endpointCandidates = RecordingOutputEndpointResolver.ResolveCandidates(
                selectedOutput,
                AudioOutputDevices);
            if (endpointCandidates.Count == 0)
            {
                throw new InvalidOperationException(selectedOutput.IsAsio
                    ? $"No se encontró el endpoint WASAPI/WDM asociado a {selectedOutput.Name}. " +
                      "Ese endpoint es necesario para capturar YouTube sin tocar WebView2."
                    : $"La salida {selectedOutput.Name} ya no está disponible para loopback.");
            }

            workDirectory = Path.Combine(
                AppPaths.RecordingWork,
                $"physical-output-{Guid.NewGuid():N}");
            preparedEndpointCapture = OutputEndpointCaptureSession.Prepare(
                endpointCandidates,
                workDirectory);

            // ASIO no aparece en el mezclador de Windows: conservamos la grabación interna de
            // Drumless (instrumentos, entradas y master) y sumaremos después el endpoint WDM donde
            // suena YouTube. Con WASAPI el loopback del endpoint ya contiene la mezcla completa.
            if (selectedOutput.IsAsio)
            {
                await _audio.StartRecordingAsync(Path.Combine(workDirectory, "internal.wav"));
                internalStarted = true;
            }

            var timelineStart = Stopwatch.GetTimestamp();
            preparedEndpointCapture.Start(timelineStart);

            _outputEndpointCapture = preparedEndpointCapture;
            _recordingDestinationPath = destination;
            _recordingWorkDirectory = workDirectory;
            _recordingUsesEndpointAsPrimary = !selectedOutput.IsAsio;
            _recordingInternalStarted = internalStarted;
            _youtubeWasActiveDuringRecording = _isYouTubeAudioActive;
            preparedEndpointCapture = null;
            workDirectory = null;
            destination = null;

            IsRecordingOutput = true;
            RecordingStatus = selectedOutput.IsAsio
                ? "● Grabando salida: mezcla ASIO de Drumless + salida física WDM/YouTube."
                : $"● Grabando exactamente lo que sale por {selectedOutput.Name}.";
            StatusMessage = "Grabación de salida iniciada";
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            ArgumentException or
            InvalidOperationException or
            TimeoutException or
            System.Runtime.InteropServices.COMException)
        {
            preparedEndpointCapture?.Dispose();
            if (internalStarted)
            {
                try
                {
                    var abandoned = await _audio.StopRecordingAsync();
                    TryDeleteRecordingFile(abandoned);
                }
                catch
                {
                    TryDeleteRecordingFile(destination);
                }
            }
            TryDeleteRecordingWorkDirectory(workDirectory);
            RecordingStatus = $"No se pudo iniciar la grabación de salida: {exception.Message}";
        }
        finally
        {
            _isStartingOutputRecording = false;
            _startOutputRecordingTask = null;
            NotifyRecordingAvailability();
        }
    }

    private Task StopOutputRecordingAsync(bool force = false)
    {
        if (_stopOutputRecordingTask is not null)
        {
            return _stopOutputRecordingTask;
        }
        if (!IsRecordingOutput)
        {
            return Task.CompletedTask;
        }

        // La navegación histórica a un elemento YouTube de una playlist llama a Stop justo antes
        // de cargar el vídeo. Durante una toma eso no debe cerrarla: sólo el botón Terminar, el cierre
        // de la aplicación o un fallo real de audio deben decidir el final de la grabación.
        if (!force && IsQueuedYouTubeNavigationPending())
        {
            return Task.CompletedTask;
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
        var endpointCapture = _outputEndpointCapture;
        var destination = _recordingDestinationPath;
        var workDirectory = _recordingWorkDirectory;
        var endpointIsPrimary = _recordingUsesEndpointAsPrimary;
        var internalStarted = _recordingInternalStarted;
        var youtubeWasActive = _youtubeWasActiveDuringRecording;
        string? finalTempPath = null;
        string? publishedPath = null;

        _outputEndpointCapture = null;
        _recordingDestinationPath = null;
        _recordingWorkDirectory = null;
        _recordingUsesEndpointAsPrimary = false;
        _recordingInternalStarted = false;
        _youtubeWasActiveDuringRecording = false;

        try
        {
            if (_isStoppingOutputRecording)
            {
                return;
            }
            _isStoppingOutputRecording = true;
            NotifyRecordingAvailability();

            if (endpointCapture is null || string.IsNullOrWhiteSpace(destination))
            {
                throw new InvalidOperationException("La sesión de grabación no conserva su salida física.");
            }

            var endpointStopTask = endpointCapture.StopAndSelectAsync();
            var internalStopTask = internalStarted
                ? _audio.StopRecordingAsync()
                : Task.FromResult<string?>(null);

            // Both writers must finish even when one fails, before disposal/recovery.
            await Task.WhenAll(endpointStopTask, internalStopTask);
            var endpointResult = await endpointStopTask;
            var internalPath = await internalStopTask;

            Directory.CreateDirectory(workDirectory!);
            finalTempPath = Path.Combine(workDirectory!, $"final-{Guid.NewGuid():N}.wav");

            if (endpointIsPrimary)
            {
                // En WASAPI el loopback ya es la mezcla física completa: Drumless, WebView2,
                // instrumentos aislados y cualquier otra señal realmente enviada a ese endpoint.
                await RecordingAudioMixService.RenderAsync(
                    [endpointResult.SelectedPath],
                    finalTempPath);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(internalPath) || !File.Exists(internalPath))
                {
                    throw new InvalidDataException("La mezcla interna ASIO no produjo un archivo utilizable.");
                }

                await RecordingAudioMixService.RenderAsync(
                    [internalPath, endpointResult.SelectedPath],
                    finalTempPath);
            }

            publishedPath = await Task.Run(() => RecordingFileStore.Publish(finalTempPath, destination));
            IsRecordingOutput = false;

            LastRecordingTrack = _trackLibrary.RegisterRecording(publishedPath);
            SelectedLibraryTrack = LastRecordingTrack;
            RefreshLibraryPresentation();
            SaveTrackWorkspace();

            var warnings = new List<string>();
            if (internalStarted && !string.IsNullOrWhiteSpace(_audio.LastRecordingWarning))
            {
                warnings.Add(_audio.LastRecordingWarning);
            }
            if (!string.IsNullOrWhiteSpace(endpointResult.Warning))
            {
                warnings.Add(endpointResult.Warning);
            }
            if (youtubeWasActive && endpointResult.Energy < 0.000001d)
            {
                warnings.Add(
                    $"YouTube estuvo activo, pero no se detectó señal en el endpoint capturado " +
                    $"({endpointResult.Device.Name}).");
            }

            RecordingStatus = $"Toma guardada y añadida a la biblioteca: {LastRecordingTrack.Title}" +
                              (warnings.Count == 0
                                  ? string.Empty
                                  : $" · {string.Join(" · ", warnings)}");
            StatusMessage = warnings.Count == 0
                ? "Grabación finalizada"
                : "Grabación finalizada con avisos";
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            InvalidDataException or
            TimeoutException or
            System.Runtime.InteropServices.COMException)
        {
            IsRecordingOutput = false;
            RecordingFileStore.WriteRecoveryNote(workDirectory, destination, exception);
            RecordingStatus = publishedPath is not null
                ? $"Toma guardada en {publishedPath}, pero no pudo añadirse a la biblioteca: {exception.Message}"
                : $"No se pudo guardar la toma: {exception.Message}. " +
                  $"Se conservan los archivos recuperables en {workDirectory}";
        }
        finally
        {
            endpointCapture?.Dispose();
            if (publishedPath is not null)
            {
                TryDeleteRecordingFile(finalTempPath);
                TryDeleteRecordingWorkDirectory(workDirectory);
            }
            _isStoppingOutputRecording = false;
            _stopOutputRecordingTask = null;
            NotifyRecordingAvailability();
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

    private static void TryDeleteRecordingFile(string? path)
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

    private static void TryDeleteRecordingWorkDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private void FinalizeRecordingOnShutdown()
    {
        if (_stopOutputRecordingTask is not null || !IsRecordingOutput)
        {
            return;
        }

        var endpointCapture = _outputEndpointCapture;
        var destination = _recordingDestinationPath;
        var workDirectory = _recordingWorkDirectory;
        var endpointIsPrimary = _recordingUsesEndpointAsPrimary;
        var internalStarted = _recordingInternalStarted;
        string? finalTempPath = null;
        try
        {
            var endpointResult = endpointCapture?.StopAndSelectAsync().GetAwaiter().GetResult();
            var internalPath = internalStarted
                ? _audio.StopRecordingAsync().GetAwaiter().GetResult()
                : null;

            if (endpointResult is null || string.IsNullOrWhiteSpace(destination))
            {
                return;
            }

            Directory.CreateDirectory(workDirectory!);
            finalTempPath = Path.Combine(workDirectory!, $"final-close-{Guid.NewGuid():N}.wav");
            var sources = endpointIsPrimary
                ? new[] { endpointResult.SelectedPath }
                : new[] { internalPath, endpointResult.SelectedPath }
                    .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                    .Select(path => path!)
                    .ToArray();

            RecordingAudioMixService.RenderAsync(sources, finalTempPath)
                .GetAwaiter()
                .GetResult();
            File.Copy(finalTempPath, destination, overwrite: true);
            LastRecordingTrack = _trackLibrary.RegisterRecording(destination);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidOperationException or
            InvalidDataException or
            TimeoutException or
            System.Runtime.InteropServices.COMException)
        {
        }
        finally
        {
            IsRecordingOutput = false;
            endpointCapture?.Dispose();
            TryDeleteRecordingFile(finalTempPath);
            TryDeleteRecordingWorkDirectory(workDirectory);
            _outputEndpointCapture = null;
            _recordingDestinationPath = null;
            _recordingWorkDirectory = null;
            _recordingUsesEndpointAsPrimary = false;
            _recordingInternalStarted = false;
            _youtubeWasActiveDuringRecording = false;
        }
    }
}
