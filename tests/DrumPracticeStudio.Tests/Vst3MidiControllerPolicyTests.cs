using DrumPracticeStudio.Midi;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class Vst3MidiControllerPolicyTests
{
    [TestMethod]
    public void ChannelVolume_IsNotForwardedToTheInstrument()
    {
        Assert.IsFalse(Vst3MidiControllerPolicy.ShouldForward(7));
    }

    [TestMethod]
    public void HiHatPedal_AndRegularControllers_AreStillForwarded()
    {
        Assert.IsTrue(Vst3MidiControllerPolicy.ShouldForward(4));
        Assert.IsTrue(Vst3MidiControllerPolicy.ShouldForward(1));
        Assert.IsTrue(Vst3MidiControllerPolicy.ShouldForward(64));
    }

    [TestMethod]
    public void ValuesOutsideTheMidiRange_AreRejected()
    {
        Assert.IsFalse(Vst3MidiControllerPolicy.ShouldForward(-1));
        Assert.IsFalse(Vst3MidiControllerPolicy.ShouldForward(128));
    }
}
