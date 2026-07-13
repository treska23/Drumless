namespace DrumPracticeStudio.Audio;

internal static class AudioLatencySettings
{
    // 4 ms a 48 kHz equivalen a 192 muestras, el ajuste usado en la Scarlett.
    public const int RequestedLatencyMilliseconds = 4;

    // Limita la granularidad con la que el instrumento recoge nuevos eventos MIDI.
    // El proveedor divide automáticamente lecturas mayores en bloques de este tamaño.
    public const int VstMaxBlockSize = 256;

    public static int RequestedSamples(int sampleRate) =>
        (int)Math.Round(sampleRate * RequestedLatencyMilliseconds / 1_000d);
}
