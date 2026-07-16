using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class StemMixPlanTests
{
    [TestMethod]
    public void GetFileNames_AllowsArbitraryUsefulCombinations()
    {
        CollectionAssert.AreEqual(
            new[] { "drums.wav" },
            StemMixPlan.GetFileNames(StemSelection.Drums).ToArray());
        CollectionAssert.AreEqual(
            new[] { "drums.wav", "bass.wav" },
            StemMixPlan.GetFileNames(StemSelection.Drums | StemSelection.Bass).ToArray());
        CollectionAssert.AreEqual(
            new[] { "vocals.wav", "guitar.wav" },
            StemMixPlan.GetFileNames(StemSelection.Vocals | StemSelection.Guitar).ToArray());
        CollectionAssert.AreEqual(
            new[] { "bass.wav", "vocals.wav", "guitar.wav", "piano.wav", "other.wav" },
            StemMixPlan.GetFileNames(StemSelection.Drumless).ToArray());
    }

    [TestMethod]
    public void EmptySelection_IsRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            StemMixPlan.GetFileNames(StemSelection.None));
    }
}
