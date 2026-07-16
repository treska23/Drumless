namespace DrumPracticeStudio.Models;

public sealed record TempoSettings(
    double Bpm,
    double FirstBeatSeconds,
    int BeatsPerBar = 4,
    bool MetronomeEnabled = false,
    double MetronomeVolume = 0.55d,
    double AnalysisConfidence = 0d)
{
    public static TempoSettings Normalize(TempoSettings settings) => settings with
    {
        Bpm = Math.Clamp(settings.Bpm, 40d, 240d),
        FirstBeatSeconds = Math.Max(0d, settings.FirstBeatSeconds),
        BeatsPerBar = Math.Clamp(settings.BeatsPerBar, 1, 12),
        MetronomeVolume = Math.Clamp(settings.MetronomeVolume, 0d, 1d),
        AnalysisConfidence = Math.Clamp(settings.AnalysisConfidence, 0d, 1d)
    };
}

public sealed record TempoAnalysisResult(
    double Bpm,
    double FirstBeatSeconds,
    double Confidence);

public sealed record DrumHit(
    double TrackPositionSeconds,
    int MidiNote,
    int Velocity);

public sealed record DrumPerformanceResult(
    int TotalHits,
    int AccurateHits,
    int EarlyHits,
    int LateHits,
    double AccuracyPercent,
    double MeanAbsoluteErrorMilliseconds,
    double MaximumErrorMilliseconds);

public enum TempoAnalysisOrigin
{
    Manual,
    Automatic,
    ManuallyAdjusted
}

public sealed class MediaAnalysisRecord
{
    public required string MediaKey { get; init; }
    public TempoSettings? Tempo { get; set; }
    public TempoAnalysisOrigin TempoOrigin { get; set; } = TempoAnalysisOrigin.Manual;
    public DateTimeOffset? TempoUpdatedAtUtc { get; set; }
    public List<DrumPerformanceSession> PerformanceSessions { get; set; } = [];
}

public sealed record DrumPerformanceSession(
    string Id,
    DateTimeOffset FinishedAtUtc,
    bool FinishedAtNaturalEnd,
    double LatencyCompensationMilliseconds,
    int TotalHits,
    int AccurateHits,
    int EarlyHits,
    int LateHits,
    double AccuracyPercent,
    double MeanAbsoluteErrorMilliseconds,
    double MaximumErrorMilliseconds)
{
    public static DrumPerformanceSession Create(
        DrumPerformanceResult result,
        double latencyCompensationMilliseconds,
        bool finishedAtNaturalEnd,
        DateTimeOffset? finishedAtUtc = null) => new(
            Guid.NewGuid().ToString("N"),
            finishedAtUtc ?? DateTimeOffset.UtcNow,
            finishedAtNaturalEnd,
            Math.Clamp(latencyCompensationMilliseconds, -500d, 500d),
            result.TotalHits,
            result.AccurateHits,
            result.EarlyHits,
            result.LateHits,
            result.AccuracyPercent,
            result.MeanAbsoluteErrorMilliseconds,
            result.MaximumErrorMilliseconds);
}

public sealed record YouTubeMetronomeRequest(TempoSettings? Settings);
