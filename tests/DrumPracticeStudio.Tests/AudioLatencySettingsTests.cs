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
    public void VstBlockSize_Uses64SamplesForLivePads()
    {
        var field = typeof(AudioLatencySettings).GetField(nameof(AudioLatencySettings.VstMaxBlockSize));
        Assert.IsNotNull(field);
        Assert.AreEqual(64, field.GetRawConstantValue());
    }
}
