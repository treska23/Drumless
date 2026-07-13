using DrumPracticeStudio.Audio;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class AudioLatencySettingsTests
{
    [TestMethod]
    public void RequestedLatency_Matches192SamplesAt48Khz()
    {
        Assert.AreEqual(192, AudioLatencySettings.RequestedSamples(48_000));
    }

    [TestMethod]
    public void VstBlockSize_RemainsSmallEnoughForLivePads()
    {
        Assert.IsTrue(AudioLatencySettings.VstMaxBlockSize <= 256);
    }
}
