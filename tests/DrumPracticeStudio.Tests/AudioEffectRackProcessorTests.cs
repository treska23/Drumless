using DrumPracticeStudio.Audio;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class AudioEffectRackProcessorTests
{
    [TestMethod]
    public void ProcessStereo_ChangesBothChannelsAndBypassPreservesThem()
    {
        var effects = new[]
        {
            AudioEffectSlotSetting.Create(AudioEffectKind.Saturation, 0.9d)
        };
        using var rack = new AudioEffectRackProcessor(48_000, effects);
        var active = Enumerable.Repeat(0.3f, 512).ToArray();
        rack.ProcessStereo(active);
        Assert.AreNotEqual(0.3f, active[^1], 0.01f);

        rack.SetEffects(effects, bypassed: true);
        var bypassed = Enumerable.Repeat(0.3f, 512).ToArray();
        rack.ProcessStereo(bypassed);
        Assert.IsTrue(bypassed.All(sample => Math.Abs(sample - 0.3f) < 0.00001f));
    }

    [TestMethod]
    public void BusItem_EnforcesFourSlotsAndPreservesOrder()
    {
        var bus = new AudioEffectBusItem(AudioEffectBusTarget.Master);
        Assert.IsTrue(bus.AddEffect());
        Assert.IsTrue(bus.AddEffect());
        Assert.IsTrue(bus.AddEffect());
        Assert.IsTrue(bus.AddEffect());
        Assert.IsFalse(bus.AddEffect());

        var last = bus.EffectSlots[^1];
        Assert.IsTrue(bus.MoveEffect(last, -1));
        Assert.AreSame(last, bus.EffectSlots[^2]);
        Assert.AreEqual(4, bus.ToSetting().EffectiveEffects.Count);
    }
}
