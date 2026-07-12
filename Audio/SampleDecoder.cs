using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DrumPracticeStudio.Audio;

internal static class SampleDecoder
{
    public static SampleBuffer Decode(string path, int targetSampleRate)
    {
        using var reader = new AudioFileReader(path);
        ISampleProvider provider = reader;

        provider = provider.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(provider),
            2 => provider,
            _ => throw new NotSupportedException($"El sample '{Path.GetFileName(path)}' debe ser mono o estéreo.")
        };

        if (provider.WaveFormat.SampleRate != targetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, targetSampleRate);
        }

        var samples = new List<float>(targetSampleRate);
        var buffer = new float[16_384];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                samples.Add(buffer[index]);
            }
        }

        return new SampleBuffer(samples.ToArray(), targetSampleRate, 2);
    }
}
