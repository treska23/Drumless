using DrumPracticeStudio.Models;
using DrumPracticeStudio.Midi;
using DrumPracticeStudio.Services;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DrumPracticeStudio.Audio;

public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48_000;

    private readonly DrumSamplerProvider _drums;
    private readonly TrackTransportProvider _track;
    private readonly MixingSampleProvider _mixer;
    private readonly OutputRecordingSink _recording = new();
    private readonly Vst3InstrumentHost _vstInstrument = new();
    private DirectVst3Instrument? _directVstInstrument;
    private AudioOutputSession? _output;
    private bool _externalInstrumentSelected;
    private string? _recordingDestination;
    private string? _recordingJobRoot;
    private string? _isolatedVstRecordingPath;

    public AudioEngine()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
        _drums = new DrumSamplerProvider(format);
        _track = new TrackTransportProvider(format);
        _mixer = new MixingSampleProvider([_track, _drums]) { ReadFully = true };
        _vstInstrument.Exited += (_, message) => VstInstrumentExited?.Invoke(this, message);
        _vstInstrument.EditorClosed += (_, _) => VstEditorClosed?.Invoke(this, EventArgs.Empty);

        try
        {
            SelectOutputDevice(null);
        }
        catch (Exception exception)
        {
            Status = $"Audio no disponible: {exception.Message}";
        }
    }

    public event EventHandler<string>? VstInstrumentExited;
    public event EventHandler? VstEditorClosed;

    public bool IsAvailable { get; private set; }
    public string Status { get; private set; } = "Audio no inicializado";
    public string? OutputDeviceId { get; private set; }
    public string? OutputDeviceName { get; private set; }
    public int? AudioInputChannelIndex => _output?.InputChannelIndex;
    public IReadOnlyList<AudioInputMonitorSetting> AudioInputMonitorSettings =>
        _output?.InputMonitorSettings ?? [];
    public bool IsAudioInputMonitoringActive => _output?.IsInputMonitoringActive == true;
    public IReadOnlyList<AudioInputChannelItem> AudioInputChannels =>
        _output?.InputChannels ?? [];
    public bool IsTrackPlaying => _track.IsPlaying;
    public TrackPlaybackState PlaybackState => _track.PlaybackState;
    public TimeSpan TrackPosition => _track.Position;
    public TimeSpan TrackDuration => _track.Duration;
    public int OutputLatencyMilliseconds => _output?.LatencyMilliseconds ?? 0;
    public bool IsVstInstrumentLoaded => _directVstInstrument is not null || _vstInstrument.IsLoaded;
    public bool IsDirectVstInstrumentLoaded => _directVstInstrument is not null;
    public bool IsExternalInstrumentSelected => _externalInstrumentSelected;
    public string? VstInstrumentName =>
        _directVstInstrument?.DisplayName ?? _vstInstrument.DisplayName;
    public string VstAudioStatus => _directVstInstrument is { } direct && _output is { } output
        ? DescribeVstOutput(output, direct.LatencySamples)
        : _vstInstrument.AudioStatus;
    public IReadOnlyList<string> VstPrograms =>
        _directVstInstrument?.Programs ?? _vstInstrument.Programs;
    public int CurrentVstProgram =>
        _directVstInstrument?.CurrentProgram ?? _vstInstrument.CurrentProgram;

    public async Task LoadKitAsync(DrumKit kit, CancellationToken cancellationToken = default)
    {
        var loaded = await KitLoader.LoadAsync(kit, SampleRate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _drums.SetKit(loaded);
    }

    public void Trigger(string articulation, int velocity, int midiNote, int midiChannel = 1)
    {
        if (_externalInstrumentSelected)
        {
            if (_directVstInstrument is not null)
            {
                _directVstInstrument.SendNoteOn(midiNote, velocity, midiChannel);
            }
            else if (_vstInstrument.IsLoaded)
            {
                _vstInstrument.SendNoteOn(midiNote, velocity, midiChannel);
            }
            return;
        }

        _drums.Trigger(articulation, velocity);
    }

    public void SendNoteOff(int midiNote, int velocity, int midiChannel = 1)
    {
        if (_directVstInstrument is not null)
        {
            _directVstInstrument.SendNoteOff(midiNote, velocity, midiChannel);
        }
        else
        {
            _vstInstrument.SendNoteOff(midiNote, velocity, midiChannel);
        }
    }

    public void SendControlChange(int controller, int value)
    {
        if (Vst3MidiControllerPolicy.ShouldForward(controller))
        {
            if (_directVstInstrument is not null)
            {
                _directVstInstrument.SendControlChange(controller, value);
            }
            else
            {
                _vstInstrument.SendControlChange(controller, value);
            }
        }
    }

    public void PanicVstInstrument()
    {
        if (_directVstInstrument is not null)
        {
            _directVstInstrument.Panic();
        }
        else
        {
            _vstInstrument.Panic();
        }
    }

    public void SelectOutputDevice(
        string? deviceId,
        int? inputChannelIndex = null,
        float inputGain = 0.8f) =>
        SelectOutputDevice(
            deviceId,
            inputChannelIndex is { } channel
                ? [new AudioInputMonitorSetting(channel, inputGain)]
                : []);

    public void SelectOutputDevice(
        string? deviceId,
        IReadOnlyList<AudioInputMonitorSetting> inputMonitorSettings)
    {
        ArgumentNullException.ThrowIfNull(inputMonitorSettings);
        if (AudioOutputDeviceId.TryGetAsioDriverName(deviceId, out _) &&
            _vstInstrument.IsLoaded)
        {
            throw new InvalidOperationException(
                "Para activar ASIO directo con un VST3 que ya está aislado, pulsa primero " +
                "«Usar motor interno», selecciona ASIO y vuelve a cargar el instrumento.");
        }

        var previous = _output;
        var exclusiveTransition =
            AudioOutputDeviceId.TryGetAsioDriverName(deviceId, out _) ||
            previous?.IsAsio == true;
        if (exclusiveTransition)
        {
            previous?.Stop();
        }

        AudioOutputSession? replacement = null;
        try
        {
            replacement = AudioOutputSession.Open(
                _mixer,
                deviceId,
                inputMonitorSettings,
                _recording);
            if (!exclusiveTransition)
            {
                previous?.Stop();
            }
            replacement.Play();
        }
        catch
        {
            replacement?.Dispose();
            previous?.Play();
            throw;
        }

        var activeOutput = replacement ??
                           throw new InvalidOperationException("La salida de audio no se inicializó.");
        _output = activeOutput;
        OutputDeviceId = activeOutput.DeviceId;
        OutputDeviceName = activeOutput.DeviceName;
        Status = DescribeOutput(
            activeOutput,
            _directVstInstrument is null ? "motor interno" : "VST3 directo");
        IsAvailable = true;
        if (_vstInstrument.IsLoaded)
        {
            _vstInstrument.SetOutputDevice(activeOutput.DeviceId);
        }
        previous?.Dispose();
    }

    public void SelectAudioInputChannel(int? channelIndex, float gain) =>
        SelectAudioInputChannels(
            channelIndex is { } channel
                ? [new AudioInputMonitorSetting(channel, gain)]
                : []);

    public void SelectAudioInputChannels(IReadOnlyList<AudioInputMonitorSetting> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_output is not { IsAsio: true } previous ||
            string.IsNullOrWhiteSpace(OutputDeviceId))
        {
            if (settings.Count == 0)
            {
                return;
            }
            throw new InvalidOperationException(
                "La monitorización directa necesita una salida ASIO seleccionada.");
        }

        var normalized = settings
            .GroupBy(setting => setting.ChannelIndex)
            .Select(group => group.Last())
            .OrderBy(setting => setting.ChannelIndex)
            .ToArray();
        var previousSettings = previous.InputMonitorSettings;
        if (previousSettings.Select(setting => setting.ChannelIndex)
            .SequenceEqual(normalized.Select(setting => setting.ChannelIndex)))
        {
            foreach (var setting in normalized)
            {
                previous.SetInputGain(setting.ChannelIndex, setting.Gain);
            }
            return;
        }

        var deviceId = OutputDeviceId;
        previous.Stop();
        previous.Dispose();
        _output = null;
        AudioOutputSession? replacement = null;
        try
        {
            replacement = AudioOutputSession.Open(_mixer, deviceId, normalized, _recording);
            replacement.Play();
        }
        catch
        {
            replacement?.Dispose();
            try
            {
                replacement = AudioOutputSession.Open(_mixer, deviceId, previousSettings, _recording);
                replacement.Play();
                _output = replacement;
                Status = DescribeOutput(
                    replacement,
                    _directVstInstrument is null ? "motor interno" : "VST3 directo");
            }
            catch
            {
                replacement?.Dispose();
                IsAvailable = false;
                Status = "Audio ASIO no disponible";
            }
            throw;
        }

        _output = replacement;
        Status = DescribeOutput(
            replacement,
            _directVstInstrument is null ? "motor interno" : "VST3 directo");
        IsAvailable = true;
    }

    public void SetAudioInputGain(float gain) =>
        _output?.SetInputGain(gain);

    public void SetAudioInputGain(int channelIndex, float gain) =>
        _output?.SetInputGain(channelIndex, gain);

    public async Task LoadVstInstrumentAsync(
        Vst3InstrumentItem instrument,
        CancellationToken cancellationToken = default)
    {
        UnloadVstInstrument();
        _externalInstrumentSelected = true;
        _drums.StopAll();

        if (_output?.IsAsio == true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectVst3Instrument? direct = null;
            try
            {
                direct = DirectVst3Instrument.Load(instrument, SampleRate);
                cancellationToken.ThrowIfCancellationRequested();
                direct.EditorClosed += OnDirectVstEditorClosed;
                _output.Stop();
                _mixer.AddMixerInput(direct.Provider);
                _directVstInstrument = direct;
                _output.Play();
                Status = DescribeOutput(_output, "VST3 directo");
                return;
            }
            catch
            {
                _directVstInstrument = null;
                if (direct is not null)
                {
                    direct.EditorClosed -= OnDirectVstEditorClosed;
                    _mixer.RemoveMixerInput(direct.Provider);
                    direct.Dispose();
                }
                _output.Play();
                _externalInstrumentSelected = false;
                throw;
            }
        }

        await _vstInstrument.LoadAsync(
            instrument,
            SampleRate,
            OutputDeviceId,
            cancellationToken);
        Status = _vstInstrument.AudioStatus;
    }

    public void SelectVstProgram(int programIndex)
    {
        if (_directVstInstrument is not null)
        {
            _directVstInstrument.SelectProgram(programIndex);
        }
        else
        {
            _vstInstrument.SelectProgram(programIndex);
        }
    }

    public void LoadVstPreset(string path)
    {
        if (_directVstInstrument is not null)
        {
            _directVstInstrument.LoadPreset(path);
        }
        else
        {
            _vstInstrument.LoadPreset(path);
        }
    }

    public void SaveVstPreset(string path)
    {
        if (_directVstInstrument is not null)
        {
            _directVstInstrument.SavePreset(path);
        }
        else if (_vstInstrument.IsLoaded)
        {
            _vstInstrument.SavePreset(path);
        }
    }

    public bool OpenVstEditor() =>
        _directVstInstrument?.OpenEditor() ?? _vstInstrument.OpenEditor();

    public void UnloadVstInstrument()
    {
        _externalInstrumentSelected = false;
        if (_directVstInstrument is { } direct)
        {
            _directVstInstrument = null;
            direct.EditorClosed -= OnDirectVstEditorClosed;
            _output?.Stop();
            _mixer.RemoveMixerInput(direct.Provider);
            direct.Dispose();
            _output?.Play();
        }
        _vstInstrument.Unload();
    }
    public void Choke(string group) => _drums.Choke(group);
    public Task<long> LoadTrackAsync(string path, CancellationToken cancellationToken = default) => _track.LoadAsync(path, cancellationToken);
    public long PlayTrack() => _track.Play();
    public void PauseTrack() => _track.Pause();
    public void StopTrack() => _track.Stop();
    public void UnloadTrack() => _track.Unload();
    public long SeekTrack(TimeSpan position) => _track.Seek(position);
    public void SetTrackVolume(float volume) => _track.SetVolume(volume);
    public void ConfigureMetronome(TempoSettings? settings) =>
        _track.ConfigureMetronome(settings);
    public bool TryDequeueTrackEnded(out TrackEndedNotification notification) =>
        _track.TryDequeueTrackEnded(out notification);
    public bool IsRecording => _recording.IsRecording;
    public string? LastRecordingWarning { get; private set; }
    public async Task StartRecordingAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Ya hay una grabación en curso.");
        }

        var destination = Path.GetFullPath(path);
        LastRecordingWarning = null;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
        if (!_vstInstrument.IsLoaded)
        {
            _recording.Start(destination, format);
            _recordingDestination = destination;
            return;
        }

        var jobRoot = Path.Combine(AppPaths.RecordingWork, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobRoot);
        var mainPath = Path.Combine(jobRoot, "main.wav");
        var vstPath = Path.Combine(jobRoot, "isolated-vst.wav");
        try
        {
            await _vstInstrument.StartRecordingAsync(vstPath, cancellationToken);
            _recording.Start(mainPath, format);
            _recordingDestination = destination;
            _recordingJobRoot = jobRoot;
            _isolatedVstRecordingPath = vstPath;
        }
        catch
        {
            try
            {
                await _vstInstrument.StopRecordingAsync(cancellationToken);
            }
            catch
            {
            }
            TryDeleteRecordingJob(jobRoot);
            throw;
        }
    }

    public async Task<string?> StopRecordingAsync(
        CancellationToken cancellationToken = default)
    {
        var mainPath = await _recording.StopAsync();
        var destination = _recordingDestination;
        var jobRoot = _recordingJobRoot;
        var vstPath = _isolatedVstRecordingPath;
        _recordingDestination = null;
        _recordingJobRoot = null;
        _isolatedVstRecordingPath = null;
        if (mainPath is null || destination is null)
        {
            return null;
        }

        if (vstPath is null)
        {
            return mainPath;
        }

        try
        {
            await _vstInstrument.StopRecordingAsync(cancellationToken);
            await AudioFileMixService.MixAsync(
                [mainPath, vstPath],
                destination,
                cancellationToken);
            return destination;
        }
        catch (Exception exception) when (exception is
            IOException or
            InvalidDataException or
            InvalidOperationException or
            TimeoutException)
        {
            File.Copy(mainPath, destination, overwrite: false);
            LastRecordingWarning =
                $"El VST3 aislado no pudo incorporarse ({exception.Message}); " +
                "se conservó la pista y el resto del mezclador.";
            return destination;
        }
        finally
        {
            if (jobRoot is not null)
            {
                TryDeleteRecordingJob(jobRoot);
            }
        }
    }

    public void Dispose()
    {
        UnloadVstInstrument();
        _output?.Dispose();
        _track.Dispose();
        _recording.Dispose();
    }

    private void OnDirectVstEditorClosed(object? sender, EventArgs e) =>
        VstEditorClosed?.Invoke(this, EventArgs.Empty);

    private static void TryDeleteRecordingJob(string jobRoot)
    {
        try
        {
            var root = Path.GetFullPath(AppPaths.RecordingWork)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var job = Path.GetFullPath(jobRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (job.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(jobRoot))
            {
                Directory.Delete(jobRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string DescribeOutput(AudioOutputSession output, string engine)
    {
        if (output.IsAsio)
        {
            var buffer = output.BufferFrames is { } frames
                ? $" · búfer {frames} muestras"
                : string.Empty;
            var input = output.InputMonitorSettings.Count > 0
                ? $" · {output.InputMonitorSettings.Count} " +
                  (output.InputMonitorSettings.Count == 1
                      ? "entrada monitorizada"
                      : "entradas monitorizadas") +
                  (output.InputLatencyMilliseconds is { } inputLatency
                      ? $" · {inputLatency + output.LatencyMilliseconds} ms ida y vuelta"
                      : string.Empty)
                : " · entrada de audio desactivada";
            return $"Audio · {output.DeviceName} · 48 kHz · {engine} · " +
                   $"ASIO directo{buffer} · {output.LatencyMilliseconds} ms de salida{input}";
        }

        var latencyLabel = output.IsLowLatencyActive
            ? $"{output.LatencyMilliseconds} ms reales · baja latencia"
            : $"{output.LatencyMilliseconds} ms · WASAPI estándar";
        var rawLabel = output.IsRawModeActive ? " · RAW" : string.Empty;
        var reason = output.IsLowLatencyActive || string.IsNullOrWhiteSpace(output.LowLatencyUnavailableReason)
            ? string.Empty
            : $" · {output.LowLatencyUnavailableReason}";
        return $"Audio · {output.DeviceName} · 48 kHz · {engine} · {latencyLabel}{rawLabel}{reason}";
    }

    private static string DescribeVstOutput(AudioOutputSession output, uint pluginLatencySamples)
    {
        var plugin = $"plugin {pluginLatencySamples} muestras";
        if (output.IsAsio)
        {
            var buffer = output.BufferFrames is { } frames
                ? $" · búfer {frames} muestras"
                : string.Empty;
            return $"Audio VST3 · {output.DeviceName} · ASIO directo{buffer} · " +
                   $"{output.LatencyMilliseconds} ms de salida · {plugin}";
        }

        return $"Audio VST3 · {output.DeviceName} · WASAPI · " +
               $"{output.LatencyMilliseconds} ms · {plugin}";
    }
}
