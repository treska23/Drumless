using DrumPracticeStudio.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DrumPracticeStudio.Audio;

public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48_000;

    private readonly DrumSamplerProvider _drums;
    private readonly TrackTransportProvider _track;
    private readonly MixingSampleProvider _mixer;
    private readonly Vst3InstrumentHost _vstInstrument = new();
    private AudioOutputSession? _output;
    private bool _externalInstrumentSelected;

    public AudioEngine()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, 2);
        _drums = new DrumSamplerProvider(format);
        _track = new TrackTransportProvider(format);
        _mixer = new MixingSampleProvider([_track, _drums]) { ReadFully = true };
        _vstInstrument.Exited += (_, message) => VstInstrumentExited?.Invoke(this, message);

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

    public bool IsAvailable { get; private set; }
    public string Status { get; private set; } = "Audio no inicializado";
    public string? OutputDeviceId { get; private set; }
    public string? OutputDeviceName { get; private set; }
    public bool IsTrackPlaying => _track.IsPlaying;
    public TrackPlaybackState PlaybackState => _track.PlaybackState;
    public TimeSpan TrackPosition => _track.Position;
    public TimeSpan TrackDuration => _track.Duration;
    public bool IsVstInstrumentLoaded => _vstInstrument.IsLoaded;
    public bool IsExternalInstrumentSelected => _externalInstrumentSelected;
    public string? VstInstrumentName => _vstInstrument.DisplayName;
    public IReadOnlyList<string> VstPrograms => _vstInstrument.Programs;
    public int CurrentVstProgram => _vstInstrument.CurrentProgram;

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
            if (_vstInstrument.IsLoaded)
            {
                _vstInstrument.SendNoteOn(midiNote, velocity, midiChannel);
            }
            return;
        }

        _drums.Trigger(articulation, velocity);
    }

    public void SendNoteOff(int midiNote, int velocity, int midiChannel = 1) =>
        _vstInstrument.SendNoteOff(midiNote, velocity, midiChannel);

    public void SendControlChange(int controller, int value) =>
        _vstInstrument.SendControlChange(controller, value);

    public void PanicVstInstrument() => _vstInstrument.Panic();

    public void SelectOutputDevice(string? deviceId)
    {
        var replacement = AudioOutputSession.Open(_mixer, deviceId);
        var previous = _output;
        previous?.Stop();
        try
        {
            replacement.Play();
        }
        catch
        {
            replacement.Dispose();
            previous?.Play();
            throw;
        }

        _output = replacement;
        OutputDeviceId = replacement.DeviceId;
        OutputDeviceName = replacement.DeviceName;
        Status = $"Audio WASAPI · {replacement.DeviceName} · 48 kHz";
        IsAvailable = true;
        _vstInstrument.SetOutputDevice(replacement.DeviceId);
        previous?.Dispose();
    }

    public async Task LoadVstInstrumentAsync(
        Vst3InstrumentItem instrument,
        CancellationToken cancellationToken = default)
    {
        _externalInstrumentSelected = true;
        _drums.StopAll();
        await _vstInstrument.LoadAsync(
            instrument,
            SampleRate,
            OutputDeviceId,
            cancellationToken);
    }

    public void SelectVstProgram(int programIndex) =>
        _vstInstrument.SelectProgram(programIndex);

    public void LoadVstPreset(string path) =>
        _vstInstrument.LoadPreset(path);

    public bool OpenVstEditor() => _vstInstrument.OpenEditor();

    public void UnloadVstInstrument()
    {
        _externalInstrumentSelected = false;
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
    public bool TryDequeueTrackEnded(out TrackEndedNotification notification) =>
        _track.TryDequeueTrackEnded(out notification);

    public void Dispose()
    {
        UnloadVstInstrument();
        _output?.Dispose();
        _track.Dispose();
    }
}
