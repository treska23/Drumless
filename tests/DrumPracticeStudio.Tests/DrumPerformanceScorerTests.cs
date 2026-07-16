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
}
