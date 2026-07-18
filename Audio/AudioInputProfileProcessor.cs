using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Audio;

/// <summary>
/// Cadena de procesamiento mono y sin asignaciones para una entrada ASIO.
/// Los perfiles son puntos de partida seguros; el nivel de entrada continúa
/// controlándose de forma independiente en el mezclador.
/// </summary>
internal sealed class AudioInputProfileProcessor
{
    private readonly int _sampleRate;
    private readonly float[] _reverbDelay;
    private AudioInputProfileKind _profile;
    private int _reverbPosition;
    private float _highPassPreviousInput;
    private float _highPassPreviousOutput;
    private float _toneLow;
    private float _envelope;
    private float _transientEnvelope;

    public AudioInputProfileProcessor(int sampleRate, AudioInputProfileKind profile)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8_000);
        _sampleRate = sampleRate;
        _reverbDelay = new float[Math.Max(1, (int)(sampleRate * 0.037d))];
        _profile = profile;
    }

    public AudioInputProfileKind Profile
    {
        get => _profile;
        set => _profile = Enum.IsDefined(value) ? value : AudioInputProfileKind.Clean;
    }

    public float Process(float sample)
    {
        if (!float.IsFinite(sample))
        {
            return 0f;
        }

        var processed = Profile switch
        {
            AudioInputProfileKind.Voice => ProcessVoice(sample),
            AudioInputProfileKind.GuitarClean => ProcessGuitarClean(sample),
            AudioInputProfileKind.GuitarDrive => ProcessGuitarDrive(sample),
            AudioInputProfileKind.Bass => ProcessBass(sample),
            AudioInputProfileKind.Drums => ProcessDrums(sample),
            _ => sample
        };
        return Math.Clamp(processed, -1f, 1f);
    }

    private float ProcessVoice(float sample)
    {
        var value = HighPass(sample, 85f);
        value = Tone(value, lowGain: 0.92f, highGain: 1.14f, crossoverHz: 1_800f);
        value = Compress(value, threshold: 0.24f, ratio: 3.2f, makeup: 1.12f);
        return Reverb(value, wet: 0.12f, feedback: 0.24f);
    }

    private float ProcessGuitarClean(float sample)
    {
        var value = HighPass(sample, 70f);
        value = Compress(value, threshold: 0.32f, ratio: 2.4f, makeup: 1.08f);
        value = Tone(value, lowGain: 0.95f, highGain: 1.08f, crossoverHz: 1_400f);
        return Saturate(value, drive: 1.35f);
    }

    private float ProcessGuitarDrive(float sample)
    {
        var value = Gate(sample, threshold: 0.018f, attenuation: 0.08f);
        value = Saturate(value, drive: 4.2f);
        value = Tone(value, lowGain: 0.82f, highGain: 1.12f, crossoverHz: 1_250f);
        return Compress(value, threshold: 0.42f, ratio: 2.2f, makeup: 0.92f);
    }

    private float ProcessBass(float sample)
    {
        var value = HighPass(sample, 32f);
        value = Compress(value, threshold: 0.28f, ratio: 3.8f, makeup: 1.1f);
        value = Tone(value, lowGain: 1.16f, highGain: 0.9f, crossoverHz: 520f);
        return Saturate(value, drive: 1.45f);
    }

    private float ProcessDrums(float sample)
    {
        var value = HighPass(sample, 28f);
        value = Gate(value, threshold: 0.012f, attenuation: 0.18f);
        value = EnhanceTransient(value, amount: 0.2f);
        return Compress(value, threshold: 0.48f, ratio: 2.6f, makeup: 1.04f);
    }

    private float HighPass(float sample, float cutoffHz)
    {
        var rc = 1f / (2f * MathF.PI * cutoffHz);
        var dt = 1f / _sampleRate;
        var coefficient = rc / (rc + dt);
        var result = coefficient *
                     (_highPassPreviousOutput + sample - _highPassPreviousInput);
        _highPassPreviousInput = sample;
        _highPassPreviousOutput = result;
        return result;
    }

    private float Tone(float sample, float lowGain, float highGain, float crossoverHz)
    {
        var coefficient = 1f - MathF.Exp(-2f * MathF.PI * crossoverHz / _sampleRate);
        _toneLow += coefficient * (sample - _toneLow);
        var high = sample - _toneLow;
        return (_toneLow * lowGain) + (high * highGain);
    }

    private float Compress(float sample, float threshold, float ratio, float makeup)
    {
        var absolute = MathF.Abs(sample);
        var attack = 1f - MathF.Exp(-1f / (0.004f * _sampleRate));
        var release = 1f - MathF.Exp(-1f / (0.090f * _sampleRate));
        _envelope += (absolute - _envelope) * (absolute > _envelope ? attack : release);
        var gain = _envelope <= threshold || _envelope <= 0f
            ? 1f
            : (threshold + ((_envelope - threshold) / ratio)) / _envelope;
        return sample * gain * makeup;
    }

    private static float Gate(float sample, float threshold, float attenuation) =>
        MathF.Abs(sample) < threshold ? sample * attenuation : sample;

    private static float Saturate(float sample, float drive)
    {
        var normalization = MathF.Tanh(drive);
        return normalization <= 0f ? sample : MathF.Tanh(sample * drive) / normalization;
    }

    private float Reverb(float sample, float wet, float feedback)
    {
        var delayed = _reverbDelay[_reverbPosition];
        _reverbDelay[_reverbPosition] = Math.Clamp(sample + (delayed * feedback), -1f, 1f);
        _reverbPosition++;
        if (_reverbPosition >= _reverbDelay.Length)
        {
            _reverbPosition = 0;
        }
        return (sample * (1f - wet)) + (delayed * wet);
    }

    private float EnhanceTransient(float sample, float amount)
    {
        var absolute = MathF.Abs(sample);
        var coefficient = absolute > _transientEnvelope ? 0.22f : 0.006f;
        var previous = _transientEnvelope;
        _transientEnvelope += (absolute - _transientEnvelope) * coefficient;
        var transient = MathF.Max(0f, absolute - previous);
        return sample * (1f + (transient * amount * 8f));
    }
}
