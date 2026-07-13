using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class AudioInputChannelItemTests
{
    [TestMethod]
    public void DisplayName_DisabledChannelUsesSafeLabel()
    {
        var channel = new AudioInputChannelItem(null, "ignored");

        Assert.IsTrue(channel.IsDisabled);
        Assert.AreEqual("Desactivada", channel.DisplayName);
    }

    [TestMethod]
    public void DisplayName_PhysicalChannelUsesOneBasedNumberAndDriverName()
    {
        var channel = new AudioInputChannelItem(1, "Analogue 2");

        Assert.IsFalse(channel.IsDisabled);
        Assert.AreEqual("Entrada 2 · Analogue 2", channel.DisplayName);
    }
}
