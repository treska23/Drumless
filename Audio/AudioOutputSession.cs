using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class AudioOutputSession : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly IWavePlayer _player;
    private bool _disposed;

    private AudioOutputSession(
        MMDeviceEnumerator enumerator,
        MMDevice device,
        IWavePlayer player)
    {
        _enumerator = enumerator;
        _device = device;
        _player = player;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
    }

    public string DeviceId { get; }
    public string DeviceName { get; }

    public static AudioOutputSession Open(ISampleProvider provider, string? deviceId)
    {
        ArgumentNullException.ThrowIfNull(provider);

        var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        IWavePlayer? player = null;
        try
        {
            device = string.IsNullOrWhiteSpace(deviceId)
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : enumerator.GetDevice(deviceId);
            player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithEventSync()
                .WithLatency(20)
                .Build();
            player.Init(provider.ToWaveProvider());
            return new AudioOutputSession(enumerator, device, player);
        }
        catch
        {
            player?.Dispose();
            device?.Dispose();
            enumerator.Dispose();
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
