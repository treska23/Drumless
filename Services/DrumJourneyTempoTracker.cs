using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public enum DrumJourneyInstrument
{
    Kick,
    Snare,
    HiHat,
    Tom,
    Cymbal,
    Other
}

public enum DrumJourneyJudgement
{
    None,
    Perfect,
    OnTime,
    Acceptable,
    OffBeat
}

public sealed record DrumJourneyHitEvent(
    double PositionSeconds,
    int MidiNote,
    int Velocity,
    DrumJourneyInstrument Instrument,
    DrumJourneyJudgement Judgement,
    double ErrorMilliseconds,
    int Score,
    int Streak,
    int Multiplier,
    bool TempoLocked);

public sealed record DrumJourneyState(
    double PositionSeconds,
    bool IsPlaying,
    bool HasTempo,
    double Bpm,
    bool TempoLocked,
    int RecentHitCount,
    int Score,
    int Streak,
    int Multiplier,
    double AccuracyPercent,
    string Status);

public sealed record DrumJourneyEvaluation(
    DrumJourneyJudgement Judgement,
    double ErrorMilliseconds,
    int Score,
    int Streak,
    int Multiplier,
    bool TempoLocked);

/// <summary>
/// Detecta si los últimos golpes mantienen una relación estable con el tempo.
/// No intenta adivinar la batería original de la canción: sólo mide coherencia rítmica.
/// </summary>
public sealed class DrumJourneyTempoTracker
{
    private const int MinimumHitsToLock = 6;
    private const int MaximumRecentHits = 12;

    private readonly List<double> _recentHits = [];
    private bool _isLocked;
    private double _anchorSeconds;
    private int _subdivision = 1;
    private int _score;
    private int _streak;
    private int _scoredHits;
    private double _qualitySum;
    private int _offBeatRun;

    public bool IsLocked => _isLocked;
    public int Score => _score;
    public int Streak => _streak;
    public int Multiplier => ResolveMultiplier(_streak);
    public double AccuracyPercent => _scoredHits == 0 ? 0d : _qualitySum * 100d / _scoredHits;
    public int RecentHitCount => _recentHits.Count;

    public void Reset(bool clearScore = true)
    {
        _recentHits.Clear();
        _isLocked = false;
        _anchorSeconds = 0d;
        _subdivision = 1;
        _offBeatRun = 0;
        if (!clearScore)
        {
            return;
        }

        _score = 0;
        _streak = 0;
        _scoredHits = 0;
        _qualitySum = 0d;
    }

    public DrumJourneyEvaluation RegisterHit(
        double positionSeconds,
        TempoSettings? tempo,
        bool isPlaying)
    {
        if (!isPlaying || tempo is null || !double.IsFinite(positionSeconds) || positionSeconds < 0d)
        {
            return CurrentEvaluation(DrumJourneyJudgement.None, 0d);
        }

        tempo = TempoSettings.Normalize(tempo);
        var segment = tempo.GetSegmentAt(positionSeconds);
        var bpm = Math.Clamp(segment.Bpm, 30d, 320d);
        AddRecentHit(positionSeconds);

        if (!_isLocked)
        {
            TryAcquireLock(bpm);
        }

        if (!_isLocked)
        {
            return CurrentEvaluation(DrumJourneyJudgement.None, 0d);
        }

        var stepSeconds = 60d / bpm / _subdivision;
        var tolerances = GetTolerances(bpm, _subdivision);
        var errorMilliseconds = SignedGridErrorSeconds(
            positionSeconds,
            _anchorSeconds,
            stepSeconds) * 1_000d;
        var absoluteError = Math.Abs(errorMilliseconds);
        var judgement = absoluteError <= tolerances.Perfect
            ? DrumJourneyJudgement.Perfect
            : absoluteError <= tolerances.OnTime
                ? DrumJourneyJudgement.OnTime
                : absoluteError <= tolerances.Acceptable
                    ? DrumJourneyJudgement.Acceptable
                    : DrumJourneyJudgement.OffBeat;

        ApplyScore(judgement);

        if (judgement is not DrumJourneyJudgement.OffBeat)
        {
            // Sigue lentamente la fase real del intérprete para no castigar una deriva humana mínima.
            _anchorSeconds += errorMilliseconds / 1_000d * 0.06d;
            _offBeatRun = 0;
        }
        else
        {
            _offBeatRun++;
        }

        if (_recentHits.Count >= 8)
        {
            var lockRatio = CalculateInWindowRatio(
                _recentHits,
                _anchorSeconds,
                stepSeconds,
                tolerances.Acceptable);
            if (lockRatio < 0.50d || _offBeatRun >= 4)
            {
                _isLocked = false;
                _offBeatRun = 0;
            }
        }

        return CurrentEvaluation(judgement, errorMilliseconds);
    }

    public DrumJourneyState CreateState(
        double positionSeconds,
        bool isPlaying,
        TempoSettings? tempo)
    {
        var hasTempo = tempo is not null;
        var bpm = hasTempo
            ? Math.Clamp(TempoSettings.Normalize(tempo!).GetSegmentAt(Math.Max(0d, positionSeconds)).Bpm, 30d, 320d)
            : 0d;
        var status = !hasTempo
            ? "SIN TEMPO · analiza o introduce el BPM para puntuar"
            : !isPlaying
                ? "EN PAUSA · los impactos siguen reaccionando"
                : _isLocked
                    ? $"TEMPO ENLAZADO · {(_subdivision == 1 ? "NEGRAS" : "CORCHEAS")}"
                    : $"BUSCANDO TEMPO · {Math.Min(_recentHits.Count, MinimumHitsToLock)}/{MinimumHitsToLock} golpes";

        return new DrumJourneyState(
            positionSeconds,
            isPlaying,
            hasTempo,
            bpm,
            _isLocked,
            _recentHits.Count,
            _score,
            _streak,
            ResolveMultiplier(_streak),
            AccuracyPercent,
            status);
    }

    private void TryAcquireLock(double bpm)
    {
        if (_recentHits.Count < MinimumHitsToLock)
        {
            return;
        }

        var beatSeconds = 60d / bpm;
        if (_recentHits[^1] - _recentHits[0] < beatSeconds * 2d)
        {
            return;
        }

        Candidate? best = null;
        foreach (var subdivision in new[] { 1, 2 })
        {
            var stepSeconds = beatSeconds / subdivision;
            var tolerances = GetTolerances(bpm, subdivision);
            foreach (var anchor in _recentHits)
            {
                var errors = _recentHits
                    .Select(hit => Math.Abs(SignedGridErrorSeconds(hit, anchor, stepSeconds)) * 1_000d)
                    .ToArray();
                var ratio = errors.Count(error => error <= tolerances.Acceptable) / (double)errors.Length;
                var mean = errors.Average();
                var candidateScore = ratio * 100d - mean / Math.Max(1d, tolerances.OnTime) * 18d -
                                     (subdivision - 1) * 2.5d;
                var candidate = new Candidate(
                    anchor,
                    subdivision,
                    ratio,
                    mean,
                    candidateScore,
                    tolerances.OnTime);
                if (best is null || candidate.Score > best.Score)
                {
                    best = candidate;
                }
            }
        }

        if (best is null || best.Ratio < 0.72d || best.MeanError > best.OnTimeTolerance * 0.90d)
        {
            return;
        }

        _anchorSeconds = best.AnchorSeconds;
        _subdivision = best.Subdivision;
        _isLocked = true;
        _offBeatRun = 0;
    }

    private void AddRecentHit(double positionSeconds)
    {
        if (_recentHits.Count > 0 && positionSeconds < _recentHits[^1] - 0.25d)
        {
            Reset(clearScore: true);
        }

        _recentHits.Add(positionSeconds);
        if (_recentHits.Count > MaximumRecentHits)
        {
            _recentHits.RemoveAt(0);
        }
    }

    private void ApplyScore(DrumJourneyJudgement judgement)
    {
        _scoredHits++;
        var basePoints = judgement switch
        {
            DrumJourneyJudgement.Perfect => 100,
            DrumJourneyJudgement.OnTime => 70,
            DrumJourneyJudgement.Acceptable => 35,
            _ => 0
        };
        var quality = judgement switch
        {
            DrumJourneyJudgement.Perfect => 1d,
            DrumJourneyJudgement.OnTime => 0.75d,
            DrumJourneyJudgement.Acceptable => 0.40d,
            _ => 0d
        };
        _qualitySum += quality;

        if (judgement is DrumJourneyJudgement.OffBeat)
        {
            // Un golpe aislado no destruye toda la racha; varios seguidos sí la erosionan.
            _streak = Math.Max(0, _streak - 3);
            return;
        }

        _streak++;
        _score += basePoints * ResolveMultiplier(_streak);
    }

    private DrumJourneyEvaluation CurrentEvaluation(
        DrumJourneyJudgement judgement,
        double errorMilliseconds) =>
        new(
            judgement,
            errorMilliseconds,
            _score,
            _streak,
            ResolveMultiplier(_streak),
            _isLocked);

    private static int ResolveMultiplier(int streak) => streak switch
    {
        >= 40 => 4,
        >= 20 => 3,
        >= 8 => 2,
        _ => 1
    };

    private static Tolerances GetTolerances(double bpm, int subdivision)
    {
        var beatMilliseconds = 60_000d / bpm;
        var stepMilliseconds = beatMilliseconds / subdivision;
        var onTime = Math.Clamp(beatMilliseconds * 0.18d, 70d, 120d);
        onTime = Math.Min(onTime, stepMilliseconds * 0.40d);
        var perfect = Math.Clamp(onTime * 0.50d, 30d, 45d);
        var acceptable = Math.Clamp(onTime * 1.55d, 100d, 160d);
        acceptable = Math.Min(acceptable, stepMilliseconds * 0.48d);
        if (acceptable < onTime + 8d)
        {
            onTime = Math.Max(perfect + 6d, acceptable * 0.76d);
        }
        return new Tolerances(perfect, onTime, acceptable);
    }

    private static double SignedGridErrorSeconds(
        double positionSeconds,
        double anchorSeconds,
        double stepSeconds)
    {
        var step = Math.Max(0.001d, stepSeconds);
        var index = Math.Round(
            (positionSeconds - anchorSeconds) / step,
            MidpointRounding.AwayFromZero);
        var nearest = anchorSeconds + index * step;
        return positionSeconds - nearest;
    }

    private static double CalculateInWindowRatio(
        IReadOnlyList<double> hits,
        double anchorSeconds,
        double stepSeconds,
        double acceptableMilliseconds)
    {
        if (hits.Count == 0)
        {
            return 0d;
        }

        var inside = hits.Count(hit =>
            Math.Abs(SignedGridErrorSeconds(hit, anchorSeconds, stepSeconds)) * 1_000d <=
            acceptableMilliseconds);
        return inside / (double)hits.Count;
    }

    private sealed record Candidate(
        double AnchorSeconds,
        int Subdivision,
        double Ratio,
        double MeanError,
        double Score,
        double OnTimeTolerance);

    private sealed record Tolerances(
        double Perfect,
        double OnTime,
        double Acceptable);
}
