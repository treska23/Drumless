using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class DrumJourneyTempoTrackerTests
{
    [TestMethod]
    public void RegularQuarterNotes_AcquireTempoLockAndScore()
    {
        var tracker = new DrumJourneyTempoTracker();
        var tempo = new TempoSettings(120d, 0d);
        DrumJourneyEvaluation? last = null;

        foreach (var position in new[] { 0d, 0.5d, 1d, 1.5d, 2d, 2.5d })
        {
            last = tracker.RegisterHit(position, tempo, isPlaying: true);
        }

        Assert.IsNotNull(last);
        Assert.IsTrue(last.TempoLocked);
        Assert.AreEqual(DrumJourneyJudgement.Perfect, last.Judgement);
        Assert.IsGreaterThan(0, last.Score);
    }

    [TestMethod]
    public void EightyMillisecondsLate_RemainsOnTimeAtOneHundredTwentyBpm()
    {
        var tracker = new DrumJourneyTempoTracker();
        var tempo = new TempoSettings(120d, 0d);
        foreach (var position in new[] { 0d, 0.5d, 1d, 1.5d, 2d, 2.5d })
        {
            tracker.RegisterHit(position, tempo, isPlaying: true);
        }

        var result = tracker.RegisterHit(3.08d, tempo, isPlaying: true);

        Assert.IsTrue(result.TempoLocked);
        Assert.AreEqual(DrumJourneyJudgement.OnTime, result.Judgement);
        Assert.AreEqual(80d, result.ErrorMilliseconds, 0.01d);
    }

    [TestMethod]
    public void SingleOffBeatHit_DoesNotImmediatelyDestroyTheLock()
    {
        var tracker = new DrumJourneyTempoTracker();
        var tempo = new TempoSettings(120d, 0d);
        foreach (var position in new[] { 0d, 0.5d, 1d, 1.5d, 2d, 2.5d })
        {
            tracker.RegisterHit(position, tempo, isPlaying: true);
        }

        var result = tracker.RegisterHit(2.74d, tempo, isPlaying: true);

        Assert.AreEqual(DrumJourneyJudgement.OffBeat, result.Judgement);
        Assert.IsTrue(result.TempoLocked);
    }

    [TestMethod]
    public void MissingTempo_KeepsVisualizerReactiveWithoutAwardingPoints()
    {
        var tracker = new DrumJourneyTempoTracker();

        var result = tracker.RegisterHit(1d, tempo: null, isPlaying: true);
        var state = tracker.CreateState(1d, isPlaying: true, tempo: null);

        Assert.AreEqual(DrumJourneyJudgement.None, result.Judgement);
        Assert.AreEqual(0, result.Score);
        Assert.IsFalse(state.HasTempo);
        StringAssert.Contains(state.Status, "SIN TEMPO");
    }
}
