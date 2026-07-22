namespace DrumPracticeStudio.Models;

public sealed record TempoSegment(
    string Id,
    double StartSeconds,
    double Bpm,
    double FirstBeatSeconds,
    int BeatsPerBar = 4,
    double Confidence = 0d,
    string SourceName = "",
    string? SourceUrl = null)
{
    public static TempoSegment Create(
        double startSeconds,
        double bpm,
        double firstBeatSeconds,
        int beatsPerBar = 4,
        double confidence = 0d,
        string sourceName = "",
        string? sourceUrl = null) => Normalize(new TempoSegment(
        Guid.NewGuid().ToString("N"),
        startSeconds,
        bpm,
        firstBeatSeconds,
        beatsPerBar,
        confidence,
        sourceName,
        sourceUrl));

    public static TempoSegment Normalize(TempoSegment segment) => segment with
    {
        Id = string.IsNullOrWhiteSpace(segment.Id)
            ? Guid.NewGuid().ToString("N")
            : segment.Id,
        StartSeconds = double.IsFinite(segment.StartSeconds)
            ? Math.Max(0d, segment.StartSeconds)
            : 0d,
        Bpm = double.IsFinite(segment.Bpm)
            ? Math.Clamp(segment.Bpm, 40d, 240d)
            : 120d,
        FirstBeatSeconds = double.IsFinite(segment.FirstBeatSeconds)
            ? Math.Max(0d, segment.FirstBeatSeconds)
            : 0d,
        BeatsPerBar = Math.Clamp(segment.BeatsPerBar, 1, 12),
        Confidence = double.IsFinite(segment.Confidence)
            ? Math.Clamp(segment.Confidence, 0d, 1d)
            : 0d,
        SourceName = (segment.SourceName ?? string.Empty).Trim(),
        SourceUrl = Uri.TryCreate(segment.SourceUrl, UriKind.Absolute, out var uri) &&
                    uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null
    };
}

public sealed record TempoSettings(
    double Bpm,
    double FirstBeatSeconds,
    int BeatsPerBar = 4,
    bool MetronomeEnabled = false,
    double MetronomeVolume = 0.55d,
    double AnalysisConfidence = 0d,
    IReadOnlyList<TempoSegment>? Segments = null)
{
    public IReadOnlyList<TempoSegment> EffectiveSegments =>
        Segments is { Count: > 0 }
            ? Segments
            :
            [
                new TempoSegment(
                    "base",
                    0d,
                    Bpm,
                    FirstBeatSeconds,
                    BeatsPerBar,
                    AnalysisConfidence)
            ];

    public TempoSegment GetSegmentAt(double positionSeconds)
    {
        var position = double.IsFinite(positionSeconds) ? Math.Max(0d, positionSeconds) : 0d;
        var segments = EffectiveSegments;
        var selected = segments[0];
        for (var index = 1; index < segments.Count; index++)
        {
            if (segments[index].StartSeconds > position)
            {
                break;
            }
            selected = segments[index];
        }
        return selected;
    }

    public static TempoSettings Normalize(TempoSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var fallback = TempoSegment.Normalize(new TempoSegment(
            "base",
            0d,
            settings.Bpm,
            settings.FirstBeatSeconds,
            settings.BeatsPerBar,
            settings.AnalysisConfidence));
        var segments = (settings.Segments ?? [])
            .Where(segment => segment is not null)
            .Select(TempoSegment.Normalize)
            .OrderBy(segment => segment.StartSeconds)
            .GroupBy(segment => Math.Round(segment.StartSeconds, 6))
            .Select(group => group.Last())
            .Take(256)
            .ToList();
        if (segments.Count == 0)
        {
            segments.Add(fallback);
        }

        var first = segments[0];
        return settings with
        {
            Bpm = first.Bpm,
            FirstBeatSeconds = first.FirstBeatSeconds,
            BeatsPerBar = first.BeatsPerBar,
            MetronomeVolume = double.IsFinite(settings.MetronomeVolume)
                ? Math.Clamp(settings.MetronomeVolume, 0d, 1d)
                : 0.55d,
            AnalysisConfidence = first.Confidence,
            Segments = segments
        };
    }
}

public sealed record TempoAnalysisResult(
    double Bpm,
    double FirstBeatSeconds,
    double Confidence);

public sealed record TempoMapAnalysisResult(
    IReadOnlyList<TempoSegment> Segments,
    double OverallConfidence,
    string Summary);

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
    double MaximumErrorMilliseconds,
    int ExpectedHits = 0,
    int MissedHits = 0,
    int ExtraHits = 0,
    bool UsedReference = false);

public sealed record DrumReferenceMap(
    string Version,
    string SourcePath,
    DateTimeOffset AnalyzedAtUtc,
    double Confidence,
    IReadOnlyList<double> HitTimesSeconds)
{
    public static DrumReferenceMap Normalize(DrumReferenceMap map) => map with
    {
        Version = string.IsNullOrWhiteSpace(map.Version)
            ? Guid.NewGuid().ToString("N")
            : map.Version,
        SourcePath = map.SourcePath ?? string.Empty,
        Confidence = double.IsFinite(map.Confidence)
            ? Math.Clamp(map.Confidence, 0d, 1d)
            : 0d,
        HitTimesSeconds = (map.HitTimesSeconds ?? [])
            .Where(time => double.IsFinite(time) && time >= 0d)
            .Order()
            .Distinct()
            .Take(100_000)
            .ToArray()
    };
}

public enum TempoAnalysisOrigin
{
    Manual,
    Automatic,
    ManuallyAdjusted,
    OnlineSource
}

public sealed class MediaAnalysisRecord
{
    public required string MediaKey { get; init; }
    public TempoSettings? Tempo { get; set; }
    public TempoAnalysisOrigin TempoOrigin { get; set; } = TempoAnalysisOrigin.Manual;
    public DateTimeOffset? TempoUpdatedAtUtc { get; set; }
    public SongStructureMap? SongStructure { get; set; }
    public ChordSheetDocument? ChordSheet { get; set; }
    public DrumReferenceMap? DrumReference { get; set; }
    public SongEffectProfile? SongEffectProfile { get; set; }
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
    double MaximumErrorMilliseconds,
    int ExpectedHits = 0,
    int MissedHits = 0,
    int ExtraHits = 0,
    string? ReferenceVersion = null)
{
    public static DrumPerformanceSession Create(
        DrumPerformanceResult result,
        double latencyCompensationMilliseconds,
        bool finishedAtNaturalEnd,
        DateTimeOffset? finishedAtUtc = null,
        string? referenceVersion = null) => new(
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
            result.MaximumErrorMilliseconds,
            result.ExpectedHits,
            result.MissedHits,
            result.ExtraHits,
            result.UsedReference ? referenceVersion : null);
}

public sealed record YouTubeMetronomeRequest(TempoSettings? Settings);

public sealed record TempoSourceCandidate(
    string Id,
    double Bpm,
    string Title,
    string SourceName,
    string SourceUrl,
    string Evidence,
    double Confidence,
    string? OllamaAssessment = null);
