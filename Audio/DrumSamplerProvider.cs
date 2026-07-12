using System.Collections.Concurrent;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class DrumSamplerProvider : ISampleProvider
{
    private readonly ConcurrentQueue<SamplerCommand> _commands = new();
    private readonly List<SampleVoice> _voices = new(96);
    private readonly Dictionary<string, int> _roundRobin = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxVoices;
    private LoadedKit? _kit;

    public DrumSamplerProvider(WaveFormat waveFormat, int maxVoices = 96)
    {
        WaveFormat = waveFormat;
        _maxVoices = maxVoices;
    }

    public WaveFormat WaveFormat { get; }

    public void SetKit(LoadedKit kit) => _commands.Enqueue(new SwapKitCommand(kit));

    public void Trigger(string articulation, int velocity) =>
        _commands.Enqueue(new TriggerCommand(articulation, Math.Clamp(velocity, 1, 127)));

    public void Choke(string group) => _commands.Enqueue(new ChokeCommand(group));

    public int Read(Span<float> buffer)
    {
        buffer.Clear();
        ProcessCommands();

        for (var voiceIndex = _voices.Count - 1; voiceIndex >= 0; voiceIndex--)
        {
            var voice = _voices[voiceIndex];
            voice.MixInto(buffer);
            if (voice.IsFinished)
            {
                _voices.RemoveAt(voiceIndex);
            }
        }

        return buffer.Length;
    }

    private void ProcessCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            switch (command)
            {
                case SwapKitCommand swap:
                    _kit = swap.Kit;
                    _roundRobin.Clear();
                    break;
                case TriggerCommand trigger:
                    StartVoice(trigger.Articulation, trigger.Velocity);
                    break;
                case ChokeCommand choke:
                    ChokeVoices(choke.Group);
                    break;
            }
        }
    }

    private void StartVoice(string articulation, int velocity)
    {
        if (_kit is null || !_kit.Pads.TryGetValue(articulation, out var pad))
        {
            return;
        }

        var layer = pad.Layers.FirstOrDefault(candidate =>
                        velocity >= candidate.MinVelocity && velocity <= candidate.MaxVelocity)
                    ?? pad.Layers.OrderBy(candidate =>
                        Math.Min(Math.Abs(candidate.MinVelocity - velocity), Math.Abs(candidate.MaxVelocity - velocity))).First();

        if (layer.Samples.Count == 0)
        {
            return;
        }

        if (pad.ChokeExisting && pad.ChokeGroup is not null)
        {
            ChokeVoices(pad.ChokeGroup);
        }

        var key = $"{articulation}:{layer.MinVelocity}";
        var nextIndex = _roundRobin.TryGetValue(key, out var current) ? current : 0;
        var sample = layer.Samples[nextIndex % layer.Samples.Count];
        _roundRobin[key] = (nextIndex + 1) % layer.Samples.Count;

        if (_voices.Count >= _maxVoices)
        {
            _voices.RemoveAt(0);
        }

        var velocityGain = MathF.Pow(velocity / 127f, 1.18f);
        _voices.Add(new SampleVoice(
            sample.Buffer,
            velocityGain * layer.Gain * sample.Gain * 0.72f,
            pad.ChokeGroup,
            WaveFormat.SampleRate));
    }

    private void ChokeVoices(string group)
    {
        foreach (var voice in _voices)
        {
            if (string.Equals(voice.ChokeGroup, group, StringComparison.OrdinalIgnoreCase))
            {
                voice.RequestStop();
            }
        }
    }

    private abstract record SamplerCommand;
    private sealed record SwapKitCommand(LoadedKit Kit) : SamplerCommand;
    private sealed record TriggerCommand(string Articulation, int Velocity) : SamplerCommand;
    private sealed record ChokeCommand(string Group) : SamplerCommand;
}

internal sealed class SampleVoice
{
    private readonly SampleBuffer _buffer;
    private readonly float _gain;
    private readonly int _fadeLength;
    private int _position;
    private int _fadeRemaining;

    public SampleVoice(SampleBuffer buffer, float gain, string? chokeGroup, int sampleRate)
    {
        _buffer = buffer;
        _gain = gain;
        ChokeGroup = chokeGroup;
        _fadeLength = Math.Max(1, sampleRate / 200); // 5 ms
    }

    public string? ChokeGroup { get; }
    public bool IsFinished => _position >= _buffer.Samples.Length || _fadeRemaining == -1;

    public void RequestStop()
    {
        if (_fadeRemaining == 0)
        {
            _fadeRemaining = _fadeLength;
        }
    }

    public void MixInto(Span<float> destination)
    {
        var available = Math.Min(destination.Length, _buffer.Samples.Length - _position);
        for (var index = 0; index < available; index++)
        {
            var fade = _fadeRemaining > 0 ? _fadeRemaining / (float)_fadeLength : 1f;
            destination[index] += _buffer.Samples[_position++] * _gain * fade;

            if (_fadeRemaining > 0 && --_fadeRemaining == 0)
            {
                _fadeRemaining = -1;
                break;
            }
        }
    }
}
