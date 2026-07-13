namespace DrumPracticeStudio.Audio;

internal static class AudioOutputTransitionPolicy
{
    public static bool RequiresVstReload(
        bool targetIsAsio,
        bool isVstLoaded,
        bool isDirectVstLoaded) =>
        targetIsAsio && isVstLoaded && !isDirectVstLoaded;
}
