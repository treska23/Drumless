using NAudio.CoreAudioApi;
using DrumPracticeStudio.Models;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class AudioOutputSession : IDisposable
{
    private readonly MMDeviceEnumerator? _enumerator;
    private readonly MMDevice? _device;
    private readonly WasapiPlayer? _wasapiPlayer;
    private readonly AsioDevice? _asioDevice;
    private readonly AsioDuplexRenderer? _asioDuplexRenderer;
    private readonly IReadOnlyList<AudioInputChannelItem> _inputChannels = [];
    private readonly bool _rawModeActive;
    private readonly int _latencyMilliseconds;
    private bool _disposed;

    private AudioOutputSession(
        MMDeviceEnumerator enumerator,
        MMDevice device,
        WasapiPlayer player,
        bool rawModeActive)
    {
        _enumerator = enumerator;
        _device = device;
        _wasapiPlayer = player;
        _rawModeActive = rawModeActive;
        _latencyMilliseconds = player.LatencyMilliseconds;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
    }

    private AudioOutputSession(
        AsioDevice device,
        string driverName,
        int sampleRate,
        IReadOnlyList<AudioInputChannelItem> inputChannels,
        AsioDuplexRenderer? duplexRenderer,
        IReadOnlyList<AudioInputMonitorSetting> inputMonitorSettings)
    {
        _asioDevice = device;
        _asioDuplexRenderer = duplexRenderer;
        _inputChannels = inputChannels;
        _latencyMilliseconds = Math.Max(
            1,
            (int)Math.Ceiling(device.OutputLatencySamples * 1_000d / sampleRate));
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
    }

    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool IsAsio => _asioDevice is not null;
    public bool IsLowLatencyActive => IsAsio || _wasapiPlayer?.LowLatencyActive == true;
    public bool IsRawModeActive => _rawModeActive;
    public int LatencyMilliseconds => _latencyMilliseconds;
    public int? BufferFrames { get; }
    public IReadOnlyList<AudioInputMonitorSetting> InputMonitorSettings { get; } = [];
    public int? InputChannelIndex => InputMonitorSettings.Count == 1
        ? InputMonitorSettings[0].ChannelIndex
        : null;
    public int? InputLatencyMilliseconds { get; }
    public bool IsInputMonitoringActive => InputMonitorSettings.Count > 0;
    public IReadOnlyList<AudioInputChannelItem> InputChannels => _inputChannels;
    public string? LowLatencyUnavailableReason => _wasapiPlayer?.LowLatencyUnavailableReason;

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
        OutputRecordingSink? recordingSink = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(inputMonitorSettings);

        if (AudioOutputDeviceId.TryGetAsioDriverName(deviceId, out var asioDriverName))
        {
            return OpenAsio(provider, asioDriverName, inputMonitorSettings, recordingSink);
        }

        var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        WasapiPlayer? player = null;
        var rawModeActive = false;
        try
        {
            device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            try
            {
                player = BuildPlayer(device, WrapRecorder(provider, recordingSink), useRawMode: true);
                rawModeActive = true;
            }
            catch (Exception)
            {
                // RAW es una mejora opcional. NAudio puede comunicar su rechazo como
                // NotSupportedException, COMException u otra excepción según el driver.
                // Reintentamos una sola vez sin RAW; si el endpoint tampoco abre así,
                // la excepción del segundo intento sí se entrega al usuario.
                player?.Dispose();
                player = BuildPlayer(device, WrapRecorder(provider, recordingSink), useRawMode: false);
            }

            return new AudioOutputSession(enumerator, device, player, rawModeActive);
        }
        catch
        {
            player?.Dispose();
            device?.Dispose();
            enumerator.Dispose();
            throw;
        }
    }

    private static AudioOutputSession OpenAsio(
        ISampleProvider provider,
        string driverName,
        IReadOnlyList<AudioInputMonitorSetting> inputMonitorSettings,
        OutputRecordingSink? recordingSink)
    {
        AsioDevice? device = null;
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

            AsioDuplexRenderer? renderer = null;
            if (normalizedSettings.Length > 0)
            {
                renderer = new AsioDuplexRenderer(
                    provider,
                    Math.Max(
                        device.Capabilities.BufferMaxSize,
                        device.Capabilities.BufferPreferredSize),
                    normalizedSettings,
                    recordingSink);
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
                device.InitPlayback(new AsioPlaybackOptions
                {
                    Source = WrapRecorder(provider, recordingSink).ToWaveProvider(),
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
                normalizedSettings);
        }
        catch
        {
            device?.Dispose();
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
        if (_asioDevice is not null)
        {
            _asioDevice.Start();
            return;
        }

        _wasapiPlayer!.Play();
    }

    public void Stop()
    {
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
            // El dispositivo puede haberse desconectado físicamente.
        }
    }

    public void SetInputGain(float gain)
    {
        foreach (var setting in InputMonitorSettings)
        {
            _asioDuplexRenderer?.SetGain(setting.ChannelIndex, gain);
        }
    }

    private static ISampleProvider WrapRecorder(
        ISampleProvider provider,
        OutputRecordingSink? sink) => sink is null
        ? provider
        : new RecordingSampleProvider(provider, sink);

    public void SetInputGain(int channelIndex, float gain) =>
        _asioDuplexRenderer?.SetGain(channelIndex, gain);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _asioDevice?.Dispose();
        _wasapiPlayer?.Dispose();
        _device?.Dispose();
        _enumerator?.Dispose();
    }

    private sealed class AsioDuplexRenderer
    {
        private readonly ISampleProvider _provider;
        private readonly float[] _interleavedOutput;
        private readonly int[] _channelIndexes;
        private readonly float[] _gains;
        private readonly OutputRecordingSink? _recordingSink;

        public AsioDuplexRenderer(
            ISampleProvider provider,
            int maximumFrames,
            IReadOnlyList<AudioInputMonitorSetting> settings,
            OutputRecordingSink? recordingSink)
        {
            _provider = provider;
            _interleavedOutput = new float[
                Math.Max(1, maximumFrames) * provider.WaveFormat.Channels];
            _channelIndexes = settings.Select(setting => setting.ChannelIndex).ToArray();
            _gains = settings.Select(setting => Math.Clamp(setting.Gain, 0f, 2f)).ToArray();
            _recordingSink = recordingSink;
        }

        public void SetGain(int channelIndex, float gain)
        {
            var position = Array.IndexOf(_channelIndexes, channelIndex);
            if (position >= 0)
            {
                Volatile.Write(ref _gains[position], Math.Clamp(gain, 0f, 2f));
            }
        }

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
            Span<float> samples = stackalloc float[_channelIndexes.Length];
            Span<float> gains = stackalloc float[_channelIndexes.Length];
            for (var frame = 0; frame < buffers.Frames; frame++)
            {
                for (var inputIndex = 0; inputIndex < _channelIndexes.Length; inputIndex++)
                {
                    samples[inputIndex] = buffers.GetInput(inputIndex)[frame];
                    gains[inputIndex] = Volatile.Read(ref _gains[inputIndex]);
                }
                var monitored = AudioInputMixMath.MixFrame(samples, gains);
                outputLeft[frame] = Math.Clamp(
                    _interleavedOutput[frame * 2] + monitored,
                    -1f,
                    1f);
                outputRight[frame] = Math.Clamp(
                    _interleavedOutput[(frame * 2) + 1] + monitored,
                    -1f,
                    1f);
            }


            if (_recordingSink is not null)
            {
                for (var frame = 0; frame < buffers.Frames; frame++)
                {
                    _interleavedOutput[frame * 2] = outputLeft[frame];
                    _interleavedOutput[(frame * 2) + 1] = outputRight[frame];
                }
                _recordingSink.Capture(_interleavedOutput.AsSpan(0, requiredSamples));
            }
        }
    }
}
