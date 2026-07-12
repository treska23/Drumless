using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Audio;

internal static class KitLoader
{
    public static Task<LoadedKit> LoadAsync(DrumKit kit, int sampleRate, CancellationToken cancellationToken = default) =>
        Task.Run(() => Load(kit, sampleRate, cancellationToken), cancellationToken);

    private static LoadedKit Load(DrumKit kit, int sampleRate, CancellationToken cancellationToken)
    {
        var cache = new Dictionary<string, SampleBuffer>(StringComparer.OrdinalIgnoreCase);
        var loadedPads = new Dictionary<string, LoadedPad>(StringComparer.OrdinalIgnoreCase);

        foreach (var pad in kit.Pads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var loadedLayers = new List<LoadedLayer>();

            foreach (var layer in pad.Layers)
            {
                var loadedSamples = new List<LoadedSample>();
                foreach (var sample in layer.Samples)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!File.Exists(sample.Path))
                    {
                        continue;
                    }

                    if (!cache.TryGetValue(sample.Path, out var buffer))
                    {
                        buffer = SampleDecoder.Decode(sample.Path, sampleRate);
                        cache[sample.Path] = buffer;
                    }

                    loadedSamples.Add(new LoadedSample(buffer, sample.Gain));
                }

                if (loadedSamples.Count > 0)
                {
                    loadedLayers.Add(new LoadedLayer(
                        layer.MinVelocity,
                        layer.MaxVelocity,
                        layer.Gain,
                        loadedSamples));
                }
            }

            if (loadedLayers.Count > 0)
            {
                loadedPads[pad.Articulation] = new LoadedPad(
                    pad.Articulation,
                    pad.ChokeGroup,
                    pad.ChokeExisting,
                    loadedLayers);
            }
        }

        return new LoadedKit(loadedPads);
    }
}
