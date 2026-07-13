using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class AudioOutputSession : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly WasapiPlayer _player;
    private readonly bool _rawModeActive;
    private bool _disposed;

    private AudioOutputSession(
        MMDeviceEnumerator enumerator,
        MMDevice device,
        WasapiPlayer player,
        bool rawModeActive)
    {
        _enumerator = enumerator;
        _device = device;
        _player = player;
        _rawModeActive = rawModeActive;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
    }

    public string DeviceId { get; }
    public string DeviceName { get; }
    public bool IsLowLatencyActive => _player.LowLatencyActive;
    public bool IsRawModeActive => _rawModeActive;
    public int LatencyMilliseconds => _player.LatencyMilliseconds;
    public string? LowLatencyUnavailableReason => _player.LowLatencyUnavailableReason;

    public static AudioOutputSession Open(ISampleProvider provider, string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(provider);

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
            catch (InvalidOperationException)
            {
                // Algunos endpoints antiguos no admiten AUDCLNT_STREAMOPTIONS_RAW.
                // La baja latencia sigue siendo preferible a rechazar la salida entera.
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

    public void Play() => _player.Play();

    public void Stop()
    {
        try
        {
            _player.Stop();
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
        _player.Dispose();
        _device.Dispose();
        _enumerator.Dispose();
    }
}
