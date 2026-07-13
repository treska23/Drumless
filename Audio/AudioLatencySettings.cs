namespace DrumPracticeStudio.Audio;

internal static class AudioLatencySettings
{
    // 4 ms a 48 kHz equivalen a 192 muestras, el ajuste usado en la Scarlett.
    public const int RequestedLatencyMilliseconds = 4;

    // Limita la granularidad con la que el instrumento recoge nuevos eventos MIDI.
    // 64 muestras son 1,33 ms a 48 kHz. El proveedor divide automáticamente
    // lecturas WASAPI mayores en bloques de este tamaño.
    public const int VstMaxBlockSize = 64;

    public static int RequestedSamples(int sampleRate) =>
        (int)Math.Round(sampleRate * RequestedLatencyMilliseconds / 1_000d);
}
