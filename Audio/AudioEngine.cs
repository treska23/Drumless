using DrumPracticeStudio.Models;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DrumPracticeStudio.Audio;

public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48_000;

    private readonly DrumSamplerProvider _drums;
    private readonly TrackTransportProvider _track;
    private IWavePlayer? _output;

    public AudioEngine()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
        _drums = new DrumSamplerProvider(format);
        _track = new TrackTransportProvider(format);
        var mixer = new MixingSampleProvider([_track, _drums]) { ReadFully = true };

        try
        {
            var wasapi = new WasapiOut(AudioClientShareMode.Shared, useEventSync: true, latency: 20);
            wasapi.Init(mixer.ToWaveProvider());
            wasapi.Play();
            _output = wasapi;
            Status = "Audio WASAPI · 48 kHz";
            IsAvailable = true;
        }
        catch (Exception wasapiError)
        {
            try
            {
                var fallback = new WaveOutEvent { DesiredLatency = 50, NumberOfBuffers = 2 };
                fallback.Init(mixer.ToWaveProvider());
                fallback.Play();
                _output = fallback;
                Status = "Audio WaveOut · modo compatible";
                IsAvailable = true;
            }
            catch (Exception fallbackError)
            {
                Status = $"Audio no disponible: {fallbackError.Message} ({wasapiError.GetType().Name})";
            }
        }
    }

    public bool IsAvailable { get; }
    public string Status { get; } = "Audio no inicializado";
    public bool IsTrackPlaying => _track.IsPlaying;
    public TrackPlaybackState PlaybackState => _track.PlaybackState;
    public TimeSpan TrackPosition => _track.Position;
    public TimeSpan TrackDuration => _track.Duration;

    public async Task LoadKitAsync(DrumKit kit, CancellationToken cancellationToken = default)
    {
        var loaded = await KitLoader.LoadAsync(kit, SampleRate, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _drums.SetKit(loaded);
    }

    public void Trigger(string articulation, int velocity) => _drums.Trigger(articulation, velocity);
    public void Choke(string group) => _drums.Choke(group);
    public Task<long> LoadTrackAsync(string path, CancellationToken cancellationToken = default) => _track.LoadAsync(path, cancellationToken);
    public long PlayTrack() => _track.Play();
    public void PauseTrack() => _track.Pause();
    public void StopTrack() => _track.Stop();
    public void UnloadTrack() => _track.Unload();
    public long SeekTrack(TimeSpan position) => _track.Seek(position);
    public void SetTrackVolume(float volume) => _track.SetVolume(volume);
    public bool TryDequeueTrackEnded(out TrackEndedNotification notification) =>
        _track.TryDequeueTrackEnded(out notification);

    public void Dispose()
    {
        _output?.Stop();
        _output?.Dispose();
        _track.Dispose();
    }
}
