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

    [TestMethod]
    public void Monitor_ProfileBuildsEditablePersistentFourSlotChain()
    {
        var monitor = new AudioInputMonitorItem
        {
            ChannelIndex = 0,
            Name = "Mic",
            Profile = AudioInputProfileKind.Voice,
            IsEnabled = true
        };

        Assert.AreEqual(4, monitor.EffectSlots.Count);
        Assert.IsFalse(monitor.AddEffect());
        monitor.MoveEffect(monitor.EffectSlots[3], -1);
        monitor.EffectSlots[0].IsEnabled = false;
        monitor.EffectsBypassed = true;

        var setting = monitor.ToSetting();
        Assert.AreEqual(4, setting.EffectiveEffects.Count);
        Assert.IsTrue(setting.EffectsBypassed);
        Assert.IsFalse(setting.EffectiveEffects[0].IsEnabled);
    }
}
