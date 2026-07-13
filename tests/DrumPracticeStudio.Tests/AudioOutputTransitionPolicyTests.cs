using DrumPracticeStudio.Audio;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class AudioOutputTransitionPolicyTests
{
    [TestMethod]
    public void IsolatedVst_ReloadsWhenChangingToAsio()
    {
        Assert.IsTrue(AudioOutputTransitionPolicy.RequiresVstReload(
            targetIsAsio: true,
            isVstLoaded: true,
            isDirectVstLoaded: false));
    }

    [TestMethod]
    public void DirectVst_DoesNotReloadWhenChangingBetweenAsioDrivers()
    {
        Assert.IsFalse(AudioOutputTransitionPolicy.RequiresVstReload(
            targetIsAsio: true,
            isVstLoaded: true,
            isDirectVstLoaded: true));
    }

    [TestMethod]
    public void InternalEngine_DoesNotNeedAVstReload()
    {
        Assert.IsFalse(AudioOutputTransitionPolicy.RequiresVstReload(
            targetIsAsio: true,
            isVstLoaded: false,
            isDirectVstLoaded: false));
    }
}
