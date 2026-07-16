using DrumPracticeStudio.Audio;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class AudioInputMixMathTests
{
    [TestMethod]
    public void MixFrame_UsesEveryInputWithIndependentGainAndClamps()
    {
        Assert.AreEqual(0.4f, AudioInputMixMath.MixFrame([0.5f, 0.25f], [0.6f, 0.4f]), 0.0001f);
        Assert.AreEqual(1f, AudioInputMixMath.MixFrame([0.8f, 0.8f], [1f, 1f]));
        Assert.AreEqual(-1f, AudioInputMixMath.MixFrame([-0.8f, -0.8f], [1f, 1f]));
    }
}
