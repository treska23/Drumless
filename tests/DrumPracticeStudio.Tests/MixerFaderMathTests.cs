using DrumPracticeStudio.Infrastructure;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class MixerFaderMathTests
{
    [TestMethod]
    public void ValueFromVerticalPoint_MapsTopMiddleAndBottomPrecisely()
    {
        Assert.AreEqual(
            1.5d,
            MixerFaderMath.ValueFromVerticalPoint(0d, 1.5d, 150d, 20d, 10d),
            0.0001d);
        Assert.AreEqual(
            0.75d,
            MixerFaderMath.ValueFromVerticalPoint(0d, 1.5d, 150d, 20d, 75d),
            0.0001d);
        Assert.AreEqual(
            0d,
            MixerFaderMath.ValueFromVerticalPoint(0d, 1.5d, 150d, 20d, 140d),
            0.0001d);
    }

    [TestMethod]
    public void ValueFromVerticalPoint_ClampsClicksOutsideTheRail()
    {
        Assert.AreEqual(
            1d,
            MixerFaderMath.ValueFromVerticalPoint(0d, 1d, 100d, 20d, -50d));
        Assert.AreEqual(
            0d,
            MixerFaderMath.ValueFromVerticalPoint(0d, 1d, 100d, 20d, 150d));
    }
}
