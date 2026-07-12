namespace DrumPracticeStudio.Audio;

public sealed record SampleBuffer(float[] Samples, int SampleRate, int Channels);

internal sealed record LoadedSample(SampleBuffer Buffer, float Gain);

internal sealed record LoadedLayer(int MinVelocity, int MaxVelocity, float Gain, IReadOnlyList<LoadedSample> Samples);

internal sealed record LoadedPad(
    string Articulation,
    string? ChokeGroup,
    bool ChokeExisting,
    IReadOnlyList<LoadedLayer> Layers);

internal sealed class LoadedKit(IReadOnlyDictionary<string, LoadedPad> pads)
{
    public IReadOnlyDictionary<string, LoadedPad> Pads { get; } = pads;
}
