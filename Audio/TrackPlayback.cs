namespace DrumPracticeStudio.Audio;

public enum TrackPlaybackState
{
    NoTrack,
    Stopped,
    Paused,
    Playing,
    Ended,
    Disposed
}

public readonly record struct TrackEndedNotification(long LoadGeneration, long RunGeneration);
