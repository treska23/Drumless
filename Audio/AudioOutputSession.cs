using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class AudioOutputSession : IDisposable
{
    private readonly MMDeviceEnumerator? _enumerator;
    private readonly MMDevice? _device;
    private readonly WasapiPlayer? _wasapiPlayer;
    private readonly AsioOut? _asioPlayer;
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

    private AudioOutputSession(AsioOut player, string driverName, int sampleRate)
    {
        _asioPlayer = player;
        _latencyMilliseconds = Math.Max(
            1,
            (int)Math.Ceiling(player.PlaybackLatency * 1_000d / sampleRate));
        DeviceId = AudioOutputDeviceId.ForAsio(driverName);
        DeviceName = driverName;
        BufferFrames = player.FramesPerBuffer;
    }

    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool IsAsio => _asioPlayer is not null;
    public bool IsLowLatencyActive => IsAsio || _wasapiPlayer?.LowLatencyActive == true;
    public bool IsRawModeActive => _rawModeActive;
    public int LatencyMilliseconds => _latencyMilliseconds;
    public int? BufferFrames { get; }
    public string? LowLatencyUnavailableReason => _wasapiPlayer?.LowLatencyUnavailableReason;

    public static AudioOutputSession Open(ISampleProvider provider, string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (AudioOutputDeviceId.TryGetAsioDriverName(deviceId, out var asioDriverName))
        {
            return OpenAsio(provider, asioDriverName);
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
                player = BuildPlayer(device, provider, useRawMode: true);
                rawModeActive = true;
            }
            catch (Exception)
            {
                // RAW es una mejora opcional. NAudio puede comunicar su rechazo como
                // NotSupportedException, COMException u otra excepción según el driver.
                // Reintentamos una sola vez sin RAW; si el endpoint tampoco abre así,
                // la excepción del segundo intento sí se entrega al usuario.
                player?.Dispose();
                player = BuildPlayer(device, provider, useRawMode: false);
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

    private static AudioOutputSession OpenAsio(ISampleProvider provider, string driverName)
    {
        AsioOut? player = null;
        try
        {
            player = new AsioOut(driverName);
            if (!player.IsSampleRateSupported(provider.WaveFormat.SampleRate))
            {
                throw new NotSupportedException(
                    $"{driverName} no admite {provider.WaveFormat.SampleRate / 1_000d:0.#} kHz.");
            }

            player.Init(provider.ToWaveProvider());
            return new AudioOutputSession(player, driverName, provider.WaveFormat.SampleRate);
        }
        catch
        {
            player?.Dispose();
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
        if (_asioPlayer is not null)
        {
            _asioPlayer.Play();
            return;
        }

        _wasapiPlayer!.Play();
    }

    public void Stop()
    {
        try
        {
            if (_asioPlayer is not null)
            {
                _asioPlayer.Stop();
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        _asioPlayer?.Dispose();
        _wasapiPlayer?.Dispose();
        _device?.Dispose();
        _enumerator?.Dispose();
    }
}
