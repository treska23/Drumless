using DrumPracticeStudio.Audio;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class AudioRecoveryPolicyTests
{
    [TestMethod]
    [DataRow("ASIO")]
    [DataRow("asio")]
    public void GetAutomaticAttemptCount_DoesNotReopenFailedAsioDriver(string backend)
    {
        Assert.AreEqual(0, AudioRecoveryPolicy.GetAutomaticAttemptCount(backend));
    }

    [TestMethod]
    [DataRow("WASAPI")]
    [DataRow(null)]
    public void GetAutomaticAttemptCount_AllowsOneSafeEndpointRetry(string? backend)
    {
        Assert.AreEqual(1, AudioRecoveryPolicy.GetAutomaticAttemptCount(backend));
    }
}
