using NAudio;
using NAudio.CoreAudioApi;
using DrumPracticeStudio.Models;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

public sealed record AudioOutputFault(
    string DeviceId,
    string DeviceName,
    string Backend,
    string Message,
    string ExceptionType,
    string ErrorCode,
    DateTimeOffset OccurredAtUtc);

internal sealed class AudioOutputSession : IDisposable
{
    private readonly MMDeviceEnumerator? _enumerator;
    private readonly MMDevice? _device;
    private readonly WasapiPlayer? _wasapiPlayer;
    private readonly AsioDevice? _asioDevice;
    private readonly AsioDuplexRenderer? _asioDuplexRenderer;
    private readonly AudioEffectRackSampleProvider? _masterEffectRack;
    private readonly IReadOnlyList<AudioInputChannelItem> _inputChannels = [];
    private readonly bool _rawModeActive;
    private readonly int _latencyMilliseconds;
    private readonly int _sampleRate = 48_000;
    private int _expectedStops;
    private int _faultReported;
    private int _isStarted;
    private bool _disposed;

    private AudioOutputSession(
        MMDeviceEnumerator enumerator,
        MMDevice device,
        WasapiPlayer player,
        bool rawModeActive,
        AudioEffectRackSampleProvider? masterEffectRack)
    {
        _enumerator = enumerator;
        _device = device;
        _wasapiPlayer = player;
        _rawModeActive = rawModeActive;
        _masterEffectRack = masterEffectRack;
        _latencyMilliseconds = player.LatencyMilliseconds;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
        player.PlaybackStopped += OnWasapiPlaybackStopped;
    }

    private AudioOutputSession(
        AsioDevice device,
        string driverName,
        int sampleRate,
        IReadOnlyList<AudioInputChannelItem> inputChannels,
        AsioDuplexRenderer? duplexRenderer,
        IReadOnlyList<AudioInputMonitorSetting> inputMonitorSettings,
        AudioEffectRackSampleProvider? masterEffectRack)
    {
        _asioDevice = device;
        _asioDuplexRenderer = duplexRenderer;
        _masterEffectRack = masterEffectRack;
        _inputChannels = inputChannels;
        _latencyMilliseconds = Math.Max(
            1,
            (int)Math.Ceiling(device.OutputLatencySamples * 1_000d / sampleRate));
        _sampleRate = sampleRate;
        DeviceId = AudioOutputDeviceId.ForAsio(driverName);
        DeviceName = driverName;
        BufferFrames = device.FramesPerBuffer;
        InputMonitorSettings = inputMonitorSettings;
        if (inputMonitorSettings.Count > 0)
        {
            InputLatencyMilliseconds = Math.Max(
                1,
                (int)Math.Ceiling(device.InputLatencySamples * 1_000d / sampleRate));
        }
        device.Stopped += OnAsioStopped;
        device.DriverResetRequest += (_, _) => ReportFault(
            new InvalidOperationException(
                "El controlador ASIO solicitó reiniciar su configuración."));
    }

    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool IsAsio => _asioDevice is not null;
    public bool IsLowLatencyActive => IsAsio || _wasapiPlayer?.LowLatencyActive == true;
    public bool IsRawModeActive => _rawModeActive;
    public int LatencyMilliseconds => _latencyMilliseconds;
    public int? BufferFrames { get; }
    public IReadOnlyList<AudioInputMonitorSetting> InputMonitorSettings { get; private set; } = [];
    public int? InputChannelIndex => InputMonitorSettings.Count == 1
        ? InputMonitorSettings[0].ChannelIndex
        : null;
    public int? InputLatencyMilliseconds { get; }
    public bool IsInputMonitoringActive => InputMonitorSettings.Count > 0;
    public IReadOnlyList<AudioInputChannelItem> InputChannels => _inputChannels;
    public string? InputEffectWarning =>
        _asioDuplexRenderer?.EffectWarnings.FirstOrDefault() ??
        _masterEffectRack?.Warning;
    public int InputEffectLatencyMilliseconds => _asioDuplexRenderer is null
        ? 0
        : (int)Math.Ceiling(
            _asioDuplexRenderer.MaximumEffectLatencySamples * 1_000d /
            Math.Max(1, _sampleRate));
    public string? LowLatencyUnavailableReason => _wasapiPlayer?.LowLatencyUnavailableReason;
    public event EventHandler<AudioOutputFault>? Faulted;

    public static AudioOutputSession Open(
        ISampleProvider provider,
        string? deviceId,
        int? inputChannelIndex = null,
        float inputGain = 0.8f)
        => Open(
            provider,
            deviceId,
            inputChannelIndex is { } channel
                ? [new AudioInputMonitorSetting(channel, inputGain)]
                : []);

    public static AudioOutputSession Open(
        ISampleProvider provider,
        string? deviceId,
        IReadOnlyList<AudioInputMonitorSetting> inputMonitorSettings,
        OutputRecordingSink? recordingSink = null,
        IReadOnlyList<AudioEffectSlotSetting>? masterEffects = null,
        bool masterEffectsBypassed = false)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(inputMonitorSettings);

        if (AudioOutputDeviceId.TryGetAsioDriverName(deviceId, out var asioDriverName))
        {
            return OpenAsio(
                provider,
                asioDriverName,
                inputMonitorSettings,
                recordingSink,
                masterEffects ?? [],
                masterEffectsBypassed);
        }

        var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        WasapiPlayer? player = null;
        AudioEffectRackSampleProvider? masterRack = null;
        var rawModeActive = false;
        try
        {
            device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            masterRack = new AudioEffectRackSampleProvider(
                provider,
                masterEffects ?? [],
                masterEffectsBypassed);
            try
            {
                player = BuildPlayer(device, WrapRecorder(masterRack, recordingSink), useRawMode: true);
                rawModeActive = true;
            }
            catch (Exception)
            {
                // RAW es una mejora opcional. NAudio puede comunicar su rechazo como
                // NotSupportedException, COMException u otra excepción según el driver.
                // Reintentamos una sola vez sin RAW; si el endpoint tampoco abre así,
                // la excepción del segundo intento sí se entrega al usuario.
                player?.Dispose();
                player = BuildPlayer(device, WrapRecorder(masterRack, recordingSink), useRawMode: false);
            }

            return new AudioOutputSession(
                enumerator,
                device,
                player,
                rawModeActive,
                masterRack);
        }
        catch
        {
            player?.Dispose();
            masterRack?.Dispose();
            device?.Dispose();
            enumerator.Dispose();
            throw;
        }
    }

    private static AudioOutputSession OpenAsio(
        ISampleProvider provider,
        string driverName,
        IReadOnlyList<AudioInputMonitorSetting> inputMonitorSettings,
        OutputRecordingSink? recordingSink,
        IReadOnlyList<AudioEffectSlotSetting> masterEffects,
        bool masterEffectsBypassed)
    {
        AsioDevice? device = null;
        AsioDuplexRenderer? renderer = null;
        AudioEffectRackSampleProvider? masterRack = null;
        try
        {
            device = AsioDevice.Open(driverName);
            if (provider.WaveFormat.Channels != 2)
            {
                throw new NotSupportedException(
                    "La monitorización ASIO directa necesita una mezcla principal estéreo.");
            }
            if (!device.IsSampleRateSupported(provider.WaveFormat.SampleRate))
            {
                throw new NotSupportedException(
                    $"{driverName} no admite {provider.WaveFormat.SampleRate / 1_000d:0.#} kHz.");
            }
            if (device.Capabilities.NbOutputChannels < provider.WaveFormat.Channels)
            {
                throw new NotSupportedException(
                    $"{driverName} no ofrece las {provider.WaveFormat.Channels} salidas necesarias.");
            }

            var inputChannels = Enumerable.Range(0, device.Capabilities.NbInputChannels)
                .Select(index => new AudioInputChannelItem(
                    index,
                    string.IsNullOrWhiteSpace(device.Capabilities.InputChannelInfos[index].name)
                        ? $"Canal {index + 1}"
                        : device.Capabilities.InputChannelInfos[index].name))
                .ToArray();

            var normalizedSettings = inputMonitorSettings
                .GroupBy(setting => setting.ChannelIndex)
                .Select(group => group.Last())
                .OrderBy(setting => setting.ChannelIndex)
                .ToArray();
            foreach (var setting in normalizedSettings)
            {
                if (setting.ChannelIndex < 0 || setting.ChannelIndex >= inputChannels.Length)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(inputMonitorSettings),
                        $"La entrada ASIO {setting.ChannelIndex + 1} no existe en {driverName}.");
                }
            }

            if (normalizedSettings.Length > 0)
            {
                renderer = new AsioDuplexRenderer(
                    provider,
                    Math.Max(
                        device.Capabilities.BufferMaxSize,
                        device.Capabilities.BufferPreferredSize),
                    normalizedSettings,
                    recordingSink,
                    masterEffects,
                    masterEffectsBypassed);
                device.InitDuplex(new AsioDuplexOptions
                {
                    InputChannels = normalizedSettings
                        .Select(setting => setting.ChannelIndex)
                        .ToArray(),
                    OutputChannels = [0, 1],
                    SampleRate = provider.WaveFormat.SampleRate,
                    Processor = renderer.Process
                });
            }
            else
            {
                masterRack = new AudioEffectRackSampleProvider(
                    provider,
                    masterEffects,
                    masterEffectsBypassed);
                device.InitPlayback(new AsioPlaybackOptions
                {
                    Source = WrapRecorder(masterRack, recordingSink).ToWaveProvider(),
                    OutputChannels = [0, 1],
                    AutoStopOnEndOfStream = false
                });
            }

            return new AudioOutputSession(
                device,
                driverName,
                provider.WaveFormat.SampleRate,
                inputChannels,
                renderer,
                normalizedSettings,
                masterRack);
        }
        catch
        {
            device?.Dispose();
            renderer?.Dispose();
            masterRack?.Dispose();
            throw;
        }
    }

    private static WasapiPlayer BuildPlayer(
        MMDevice device,
        ISampleProvider provider,
        bool useRawMode)
    {
        var builder = new WasapiPlayerBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(AudioLatencySettings.RequestedLatencyMilliseconds)
            .WithLowLatency()
            .WithMmcssThreadPriority("Pro Audio");
        if (useRawMode)
        {
            builder.WithRawMode();
        }

        var player = builder.Build();
        try
        {
            player.Init(provider.ToWaveProvider());
            return player;
        }
        catch
        {
            player.Dispose();
            throw;
        }
    }

    public void Play()
    {
        if (Volatile.Read(ref _isStarted) != 0)
        {
            return;
        }

        if (_asioDevice is not null)
        {
            _asioDevice.Start();
        }
        else
        {
            _wasapiPlayer!.Play();
        }
        Volatile.Write(ref _isStarted, 1);
        Interlocked.Exchange(ref _faultReported, 0);
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _isStarted, 0) == 0)
        {
            return;
        }

        Interlocked.Increment(ref _expectedStops);
        try
        {
            if (_asioDevice is not null)
            {
                _asioDevice.Stop();
            }
            else
            {
                _wasapiPlayer?.Stop();
            }
        }
        catch
        {
            ConsumeExpectedStop();
            // El dispositivo puede haberse desconectado físicamente.
        }
    }

    public void SetInputGain(float gain)
    {
        foreach (var setting in InputMonitorSettings)
        {
            _asioDuplexRenderer?.SetGain(setting.ChannelIndex, gain);
        }
        InputMonitorSettings = InputMonitorSettings
            .Select(setting => setting with { Gain = Math.Clamp(gain, 0f, 2f) })
            .ToArray();
    }

    private static ISampleProvider WrapRecorder(
        ISampleProvider provider,
        OutputRecordingSink? sink) => sink is null
        ? provider
        : new RecordingSampleProvider(provider, sink);

    public void SetInputGain(int channelIndex, float gain)
    {
        _asioDuplexRenderer?.SetGain(channelIndex, gain);
        InputMonitorSettings = InputMonitorSettings
            .Select(setting => setting.ChannelIndex == channelIndex
                ? setting with { Gain = Math.Clamp(gain, 0f, 2f) }
                : setting)
            .ToArray();
    }

    public void SetInputProfile(int channelIndex, AudioInputProfileKind profile)
    {
        _asioDuplexRenderer?.SetProfile(channelIndex, profile);
        InputMonitorSettings = InputMonitorSettings
            .Select(setting => setting.ChannelIndex == channelIndex
                ? setting with
                {
                    Profile = profile,
                    Effects = AudioInputEffectPresetCatalog.Create(profile),
                    EffectsBypassed = false
                }
                : setting)
            .ToArray();
    }

    public void SetInputEffects(
        int channelIndex,
        IReadOnlyList<AudioEffectSlotSetting> effects,
        bool bypassed)
    {
        _asioDuplexRenderer?.SetEffects(channelIndex, effects, bypassed);
        InputMonitorSettings = InputMonitorSettings
            .Select(setting => setting.ChannelIndex == channelIndex
                ? setting with
                {
                    Effects = effects
                        .Take(AudioEffectCatalog.MaximumSlots)
                        .Select(AudioEffectSlotSetting.Normalize)
                        .ToArray(),
                    EffectsBypassed = bypassed
                }
                : setting)
            .ToArray();
    }

    public void SetMasterEffects(
        IReadOnlyList<AudioEffectSlotSetting> effects,
        bool bypassed)
    {
        _masterEffectRack?.SetEffects(effects, bypassed);
        _asioDuplexRenderer?.SetMasterEffects(effects, bypassed);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _asioDevice?.Dispose();
        _asioDuplexRenderer?.Dispose();
        _masterEffectRack?.Dispose();
        _wasapiPlayer?.Dispose();
        _device?.Dispose();
        _enumerator?.Dispose();
    }

    private void OnWasapiPlaybackStopped(object? sender, StoppedEventArgs eventArgs) =>
        OnBackendStopped("WASAPI", eventArgs.Exception);

    private void OnAsioStopped(object? sender, StoppedEventArgs eventArgs) =>
        OnBackendStopped("ASIO", eventArgs.Exception);

    private void OnBackendStopped(string backend, Exception? exception)
    {
        if (_disposed || ConsumeExpectedStop())
        {
            return;
        }

        Volatile.Write(ref _isStarted, 0);
        ReportFault(exception ?? new InvalidOperationException(
            $"La salida {backend} se detuvo sin que el usuario lo solicitara."));
    }

    private bool ConsumeExpectedStop()
    {
        while (true)
        {
            var current = Volatile.Read(ref _expectedStops);
            if (current <= 0)
            {
                return false;
            }
            if (Interlocked.CompareExchange(ref _expectedStops, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    private void ReportFault(Exception exception)
    {
        if (_disposed || Interlocked.Exchange(ref _faultReported, 1) != 0)
        {
            return;
        }

        Faulted?.Invoke(this, new AudioOutputFault(
            DeviceId,
            DeviceName,
            IsAsio ? "ASIO" : "WASAPI",
            exception.Message,
            exception.GetType().Name,
            GetDiagnosticErrorCode(exception),
            DateTimeOffset.UtcNow));
    }

    private static string GetDiagnosticErrorCode(Exception exception) =>
        exception is MmException multimedia
            ? $"{multimedia.Result} · 0x{exception.HResult:X8}"
            : $"0x{exception.HResult:X8}";

    private sealed class AsioDuplexRenderer : IDisposable
    {
        private readonly ISampleProvider _provider;
        private readonly float[] _interleavedOutput;
        private readonly int[] _channelIndexes;
        private readonly float[] _gains;
        private readonly AudioInputProfileProcessor[] _processors;
        private readonly float[][] _inputBuffers;
        private readonly OutputRecordingSink? _recordingSink;
        private readonly AudioEffectRackProcessor _masterEffects;

        public IReadOnlyList<string> EffectWarnings => _processors
            .Select(processor => processor.ExternalWarning)
            .Append(_masterEffects.Warning)
            .Where(warning => !string.IsNullOrWhiteSpace(warning))
            .Cast<string>()
            .ToArray();
        public uint MaximumEffectLatencySamples => _processors.Length == 0
            ? _masterEffects.LatencySamples
            : Math.Max(
                _masterEffects.LatencySamples,
                _processors.Max(processor => processor.ExternalLatencySamples));

        public AsioDuplexRenderer(
            ISampleProvider provider,
            int maximumFrames,
            IReadOnlyList<AudioInputMonitorSetting> settings,
            OutputRecordingSink? recordingSink,
            IReadOnlyList<AudioEffectSlotSetting> masterEffects,
            bool masterEffectsBypassed)
        {
            _provider = provider;
            _interleavedOutput = new float[
                Math.Max(1, maximumFrames) * provider.WaveFormat.Channels];
            _channelIndexes = settings.Select(setting => setting.ChannelIndex).ToArray();
            _gains = settings.Select(setting => Math.Clamp(setting.Gain, 0f, 2f)).ToArray();
            _processors = settings
                .Select(setting => new AudioInputProfileProcessor(
                    provider.WaveFormat.SampleRate,
                    setting.Profile))
                .ToArray();
            _inputBuffers = settings
                .Select(_ => new float[Math.Max(1, maximumFrames)])
                .ToArray();
            for (var index = 0; index < settings.Count; index++)
            {
                _processors[index].SetEffects(
                    settings[index].EffectiveEffects,
                    settings[index].EffectsBypassed);
            }
            _recordingSink = recordingSink;
            _masterEffects = new AudioEffectRackProcessor(
                provider.WaveFormat.SampleRate,
                masterEffects,
                masterEffectsBypassed);
        }

        public void SetGain(int channelIndex, float gain)
        {
            var position = Array.IndexOf(_channelIndexes, channelIndex);
            if (position >= 0)
            {
                Volatile.Write(ref _gains[position], Math.Clamp(gain, 0f, 2f));
            }
        }

        public void SetProfile(int channelIndex, AudioInputProfileKind profile)
        {
            var position = Array.IndexOf(_channelIndexes, channelIndex);
            if (position >= 0)
            {
                _processors[position].Profile = profile;
            }
        }

        public void SetEffects(
            int channelIndex,
            IReadOnlyList<AudioEffectSlotSetting> effects,
            bool bypassed)
        {
            var position = Array.IndexOf(_channelIndexes, channelIndex);
            if (position >= 0)
            {
                _processors[position].SetEffects(effects, bypassed);
            }
        }

        public void SetMasterEffects(
            IReadOnlyList<AudioEffectSlotSetting> effects,
            bool bypassed) =>
            _masterEffects.SetEffects(effects, bypassed);

        public void Process(in AsioProcessBuffers buffers)
        {
            var requiredSamples = buffers.Frames * 2;
            var read = _provider.Read(_interleavedOutput.AsSpan(0, requiredSamples));
            if (read < requiredSamples)
            {
                Array.Clear(_interleavedOutput, read, requiredSamples - read);
            }

            var outputLeft = buffers.GetOutput(0);
            var outputRight = buffers.GetOutput(1);
            for (var inputIndex = 0; inputIndex < _channelIndexes.Length; inputIndex++)
            {
                var input = buffers.GetInput(inputIndex);
                var working = _inputBuffers[inputIndex].AsSpan(0, buffers.Frames);
                input[..buffers.Frames].CopyTo(working);
                _processors[inputIndex].ProcessBlock(working);
            }
            Span<float> samples = stackalloc float[_channelIndexes.Length];
            Span<float> gains = stackalloc float[_channelIndexes.Length];
            for (var frame = 0; frame < buffers.Frames; frame++)
            {
                for (var inputIndex = 0; inputIndex < _channelIndexes.Length; inputIndex++)
                {
                    samples[inputIndex] = _inputBuffers[inputIndex][frame];
                    gains[inputIndex] = Volatile.Read(ref _gains[inputIndex]);
                }
                var monitored = AudioInputMixMath.MixFrame(samples, gains);
                _interleavedOutput[frame * 2] = Math.Clamp(
                    _interleavedOutput[frame * 2] + monitored,
                    -1f,
                    1f);
                _interleavedOutput[(frame * 2) + 1] = Math.Clamp(
                    _interleavedOutput[(frame * 2) + 1] + monitored,
                    -1f,
                    1f);
            }

            _masterEffects.ProcessStereo(_interleavedOutput.AsSpan(0, requiredSamples));
            for (var frame = 0; frame < buffers.Frames; frame++)
            {
                outputLeft[frame] = _interleavedOutput[frame * 2];
                outputRight[frame] = _interleavedOutput[(frame * 2) + 1];
            }

            if (_recordingSink is not null)
            {
                _recordingSink.Capture(_interleavedOutput.AsSpan(0, requiredSamples));
            }
        }

        public void Dispose()
        {
            foreach (var processor in _processors)
            {
                processor.Dispose();
            }
            _masterEffects.Dispose();
        }
    }
}
