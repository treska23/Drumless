namespace DrumPracticeStudio.Midi;

internal static class Vst3MidiControllerPolicy
{
    // CC 7 es Channel Volume. Algunos controladores compactos (incluido el MPK Mini)
    // pueden emitir valores muy bajos al tocar o rozar sus controles. Reenviarlo sin
    // una asignación explícita acaba silenciando el instrumento VST3 completo.
    private const int ChannelVolume = 7;

    public static bool ShouldForward(int controller) =>
        controller is >= 0 and <= 127 && controller != ChannelVolume;
}
