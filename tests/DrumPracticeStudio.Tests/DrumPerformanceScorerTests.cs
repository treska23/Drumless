using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class DrumPerformanceScorerTests
{
    [TestMethod]
    public void Finish_ClassifiesAccurateEarlyAndLateHitsWithLatencyCompensation()
    {
        var scorer = new DrumPerformanceScorer();
        scorer.Start(new TempoSettings(120d, 0.25d), latencyCompensationMilliseconds: 20d);

        scorer.Record(0.270d, 36, 100); // Compensado: exactamente en el pulso.
        scorer.Record(0.195d, 38, 100); // Compensado: 75 ms adelantado.
        scorer.Record(0.345d, 42, 100); // Compensado: 75 ms atrasado.
        var result = scorer.Finish();

        Assert.AreEqual(3, result.TotalHits);
        Assert.AreEqual(1, result.AccurateHits);
        Assert.AreEqual(1, result.EarlyHits);
        Assert.AreEqual(1, result.LateHits);
        Assert.AreEqual(100d / 3d, result.AccuracyPercent, 0.01d);
    }

    [TestMethod]
    public void Grid_UsesSixteenthNotesAtTheConfiguredTempoAndPhase()
    {
        var tempo = new TempoSettings(120d, 0.25d);

        Assert.AreEqual(0d, TempoGrid.NearestGridErrorSeconds(0.25d, tempo), 1e-9d);
        Assert.AreEqual(0d, TempoGrid.NearestGridErrorSeconds(0.375d, tempo), 1e-9d);
        Assert.AreEqual(0.030d, TempoGrid.NearestGridErrorSeconds(0.405d, tempo), 1e-9d);
    }

    [TestMethod]
    public void Reference_CountsMissedAndExtraHitsWithoutDoubleMatching()
    {
        var scorer = new DrumPerformanceScorer();
        var reference = new DrumReferenceMap(
            "ref-v1",
            "drums.wav",
            DateTimeOffset.UtcNow,
            0.9d,
            [1d, 2d, 3d]);
        scorer.Start(
            new TempoSettings(120d, 0d),
            latencyCompensationMilliseconds: 0d,
            reference);
        scorer.Record(1.01d, 36, 100);
        scorer.Record(2.09d, 38, 100);
        scorer.Record(4d, 42, 100);

        var result = scorer.Finish();

        Assert.IsTrue(result.UsedReference);
        Assert.AreEqual(3, result.ExpectedHits);
        Assert.AreEqual(1, result.AccurateHits);
        Assert.AreEqual(1, result.LateHits);
        Assert.AreEqual(1, result.MissedHits);
        Assert.AreEqual(1, result.ExtraHits);
        Assert.AreEqual(25d, result.AccuracyPercent, 0.01d);
    }
}
