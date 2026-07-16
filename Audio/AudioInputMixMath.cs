namespace DrumPracticeStudio.Audio;

internal static class AudioInputMixMath
{
    public static float MixFrame(ReadOnlySpan<float> samples, ReadOnlySpan<float> gains)
    {
        if (samples.Length != gains.Length)
        {
            throw new ArgumentException("Cada muestra de entrada necesita una ganancia.");
        }

        var mixed = 0f;
        for (var index = 0; index < samples.Length; index++)
        {
            mixed += samples[index] * gains[index];
        }
        return Math.Clamp(mixed, -1f, 1f);
    }
}
