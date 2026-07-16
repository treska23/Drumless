using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed class DrumPerformanceScorer
{
    public const double AccurateToleranceMilliseconds = 45d;

    private readonly List<DrumHit> _hits = [];
    private readonly object _gate = new();

    public bool IsActive { get; private set; }
    public TempoSettings? Tempo { get; private set; }
    public double LatencyCompensationMilliseconds { get; private set; }
    public IReadOnlyList<DrumHit> Hits => _hits;

    public void Start(TempoSettings tempo, double latencyCompensationMilliseconds)
    {
        lock (_gate)
        {
            Tempo = TempoSettings.Normalize(tempo);
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
            if (Tempo is null || _hits.Count == 0)
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

    public void Cancel()
    {
        lock (_gate)
        {
            IsActive = false;
            _hits.Clear();
        }
    }
}
