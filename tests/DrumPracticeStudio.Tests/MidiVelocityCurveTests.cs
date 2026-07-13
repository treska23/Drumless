using DrumPracticeStudio.Midi;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class MidiVelocityCurveTests
{
    [TestMethod]
    public void NeutralSensitivity_PreservesVelocity()
    {
        for (var velocity = 1; velocity <= 127; velocity++)
        {
            Assert.AreEqual(velocity, MidiVelocityCurve.Apply(velocity, 50));
        }
    }

    [TestMethod]
    public void HigherSensitivity_BoostsNormalHitsWithoutChangingMaximum()
    {
        Assert.IsTrue(MidiVelocityCurve.Apply(64, 75) > 64);
        Assert.AreEqual(127, MidiVelocityCurve.Apply(127, 75));
    }

    [TestMethod]
    public void VelocityCurve_IsMonotonic()
    {
        var previous = 0;
        for (var velocity = 1; velocity <= 127; velocity++)
        {
            var current = MidiVelocityCurve.Apply(velocity, 72);
            Assert.IsTrue(current >= previous);
            previous = current;
        }
    }

    [TestMethod]
    public void NoteOffVelocity_RemainsZero()
    {
        Assert.AreEqual(0, MidiVelocityCurve.Apply(0, 100));
    }
}
