using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed class DrumPerformanceScorer
{
    public const double AccurateToleranceMilliseconds = 45d;

    private readonly List<DrumHit> _hits = [];
    private readonly object _gate = new();

    public bool IsActive { get; private set; }
    public TempoSettings? Tempo { get; private set; }
    public DrumReferenceMap? Reference { get; private set; }
    public double LatencyCompensationMilliseconds { get; private set; }
    public IReadOnlyList<DrumHit> Hits => _hits;

    public void Start(
        TempoSettings tempo,
        double latencyCompensationMilliseconds,
        DrumReferenceMap? reference = null)
    {
        lock (_gate)
        {
            Tempo = TempoSettings.Normalize(tempo);
            Reference = reference is null ? null : DrumReferenceMap.Normalize(reference);
            LatencyCompensationMilliseconds = Math.Clamp(
                latencyCompensationMilliseconds,
                -500d,
                500d);
            _hits.Clear();
            IsActive = true;
        }
    }

    public void Record(double transportPositionSeconds, int midiNote, int velocity)
    {
        lock (_gate)
        {
            if (!IsActive || Tempo is null || velocity <= 0)
            {
                return;
            }

            var compensated = Math.Max(
                0d,
                transportPositionSeconds - LatencyCompensationMilliseconds / 1000d);
            _hits.Add(new DrumHit(compensated, midiNote, velocity));
        }
    }

    public DrumPerformanceResult Finish()
    {
        lock (_gate)
        {
            IsActive = false;
            if (Tempo is null)
            {
                return new DrumPerformanceResult(0, 0, 0, 0, 0d, 0d, 0d);
            }

            if (Reference is { HitTimesSeconds.Count: > 0 } reference)
            {
                return ScoreAgainstReference(reference);
            }
            if (_hits.Count == 0)
            {
                return new DrumPerformanceResult(0, 0, 0, 0, 0d, 0d, 0d);
            }

            var errors = _hits
                .Select(hit => TempoGrid.NearestGridErrorSeconds(hit.TrackPositionSeconds, Tempo) * 1000d)
                .ToArray();
            var accurate = errors.Count(error => Math.Abs(error) <= AccurateToleranceMilliseconds);
            var early = errors.Count(error => error < -AccurateToleranceMilliseconds);
            var late = errors.Count(error => error > AccurateToleranceMilliseconds);
            return new DrumPerformanceResult(
                errors.Length,
                accurate,
                early,
                late,
                accurate * 100d / errors.Length,
                errors.Average(Math.Abs),
                errors.Max(error => Math.Abs(error)));
        }
    }

    private DrumPerformanceResult ScoreAgainstReference(DrumReferenceMap reference)
    {
        const double maximumMatchSeconds = 0.18d;
        var expected = reference.HitTimesSeconds.ToArray();
        var matched = new bool[expected.Length];
        var errors = new List<double>();
        var extra = 0;
        foreach (var hit in _hits.OrderBy(hit => hit.TrackPositionSeconds))
        {
            var insertion = Array.BinarySearch(expected, hit.TrackPositionSeconds);
            if (insertion < 0)
            {
                insertion = ~insertion;
            }
            var bestIndex = -1;
            var bestDistance = double.MaxValue;
            var start = Math.Max(0, insertion - 3);
            var end = Math.Min(expected.Length - 1, insertion + 3);
            for (var index = start; index <= end; index++)
            {
                if (matched[index])
                {
                    continue;
                }
                var distance = Math.Abs(hit.TrackPositionSeconds - expected[index]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = index;
                }
            }
            if (bestIndex < 0 || bestDistance > maximumMatchSeconds)
            {
                extra++;
                continue;
            }

            matched[bestIndex] = true;
            errors.Add((hit.TrackPositionSeconds - expected[bestIndex]) * 1_000d);
        }

        var accurate = errors.Count(error =>
            Math.Abs(error) <= AccurateToleranceMilliseconds);
        var early = errors.Count(error => error < -AccurateToleranceMilliseconds);
        var late = errors.Count(error => error > AccurateToleranceMilliseconds);
        var missed = matched.Count(value => !value);
        var denominator = expected.Length + extra;
        return new DrumPerformanceResult(
            _hits.Count,
            accurate,
            early,
            late,
            denominator == 0 ? 0d : accurate * 100d / denominator,
            errors.Count == 0 ? 0d : errors.Average(Math.Abs),
            errors.Count == 0 ? 0d : errors.Max(error => Math.Abs(error)),
            expected.Length,
            missed,
            extra,
            UsedReference: true);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            IsActive = false;
            _hits.Clear();
            Reference = null;
        }
    }
}
