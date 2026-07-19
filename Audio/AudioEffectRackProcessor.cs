using DrumPracticeStudio.Models;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class AudioEffectRackProcessor : IDisposable
{
    private readonly object _gate = new();
    private readonly int _sampleRate;
    private List<ExternalEffect> _external = [];
    private string _externalSignature = string.Empty;
    private bool _bypassed;
    private string? _warning;

    public AudioEffectRackProcessor(
        int sampleRate,
        IReadOnlyList<AudioEffectSlotSetting>? effects = null,
        bool bypassed = false)
    {
        _sampleRate = sampleRate;
        SetEffects(effects ?? [], bypassed);
    }

    public string? Warning => Volatile.Read(ref _warning);
    public uint LatencySamples
    {
        get
        {
            lock (_gate)
            {
                return (uint)_external.Sum(effect =>
                    (long)effect.Processor.TotalLatencySamples);
            }
        }
    }

    public void SetEffects(
        IReadOnlyList<AudioEffectSlotSetting> effects,
        bool bypassed)
    {
        var normalized = effects
            .Where(effect =>
                effect.Kind == AudioEffectKind.ExternalVst3 &&
                effect.ExternalVst3 is not null)
            .Take(AudioEffectCatalog.MaximumSlots)
            .Select(AudioEffectSlotSetting.Normalize)
            .ToArray();
        lock (_gate)
        {
            _bypassed = bypassed;
            ConfigureExternal(normalized);
        }
    }

    public void ProcessStereo(Span<float> interleaved)
    {
        var frames = interleaved.Length / 2;
        if (frames == 0)
        {
            return;
        }
        lock (_gate)
        {
            if (_bypassed)
            {
                return;
            }
            foreach (var effect in _external)
            {
                effect.Processor.ProcessStereo(interleaved[..(frames * 2)], effect.Mix);
                if (effect.Processor.Failure is { } failure)
                {
                    Volatile.Write(ref _warning, failure);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var effect in _external)
            {
                effect.Processor.Dispose();
            }
            _external.Clear();
        }
    }

    private void ConfigureExternal(IReadOnlyList<AudioEffectSlotSetting> effects)
    {
        var requested = effects
            .Where(effect =>
                effect.IsEnabled &&
                effect.Kind == AudioEffectKind.ExternalVst3 &&
                effect.ExternalVst3 is not null)
            .ToArray();
        var signature = string.Join(
            "|",
            requested.Select(effect =>
                $"{effect.Id}:{effect.ExternalVst3!.ModulePath}:{effect.ExternalVst3.ClassId}:" +
                effect.ExternalVst3.PresetPath));
        if (string.Equals(signature, _externalSignature, StringComparison.Ordinal))
        {
            for (var index = 0; index < requested.Length && index < _external.Count; index++)
            {
                _external[index] = _external[index] with { Mix = (float)requested[index].Mix };
            }
            return;
        }

        foreach (var effect in _external)
        {
            effect.Processor.Dispose();
        }
        _external = [];
        _externalSignature = signature;
        Volatile.Write(ref _warning, null);
        foreach (var setting in requested)
        {
            try
            {
                _external.Add(new ExternalEffect(
                    (float)setting.Mix,
                    IsolatedVst3EffectProcessor.Start(setting.ExternalVst3!, _sampleRate)));
            }
            catch (Exception exception)
            {
                Volatile.Write(
                    ref _warning,
                    $"{setting.ExternalVst3!.Name} quedó en bypass: {exception.Message}");
            }
        }
    }

    private sealed record ExternalEffect(
        float Mix,
        IsolatedVst3EffectProcessor Processor);
}

internal sealed class AudioEffectRackSampleProvider : ISampleProvider, IDisposable
{
    private readonly ISampleProvider _source;
    private readonly AudioEffectRackProcessor _processor;

    public AudioEffectRackSampleProvider(
        ISampleProvider source,
        IReadOnlyList<AudioEffectSlotSetting>? effects = null,
        bool bypassed = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.WaveFormat.Channels != 2)
        {
            throw new NotSupportedException("Los buses de efectos necesitan audio estéreo.");
        }
        _source = source;
        _processor = new AudioEffectRackProcessor(
            source.WaveFormat.SampleRate,
            effects,
            bypassed);
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }
    public string? Warning => _processor.Warning;
    public uint LatencySamples => _processor.LatencySamples;

    public int Read(Span<float> buffer)
    {
        var read = _source.Read(buffer);
        _processor.ProcessStereo(buffer[..(read - (read % WaveFormat.Channels))]);
        return read;
    }

    public void SetEffects(
        IReadOnlyList<AudioEffectSlotSetting> effects,
        bool bypassed) => _processor.SetEffects(effects, bypassed);

    public void Dispose() => _processor.Dispose();
}
