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
    public void Monitor_ProfilesStayCleanAndOnlyConfiguredVst3SlotsPersist()
    {
        var monitor = new AudioInputMonitorItem
        {
            ChannelIndex = 0,
            Name = "Mic",
            Profile = AudioInputProfileKind.Voice,
            IsEnabled = true
        };

        Assert.AreEqual(0, monitor.EffectSlots.Count);
        for (var index = 0; index < AudioEffectCatalog.MaximumSlots; index++)
        {
            Assert.IsTrue(monitor.AddEffect());
            monitor.EffectSlots[index].ExternalVst3 = Effect($"FX {index}");
        }
        Assert.IsFalse(monitor.AddEffect());
        var last = monitor.EffectSlots[3];
        Assert.IsTrue(monitor.MoveEffect(last, -1));
        Assert.AreSame(last, monitor.EffectSlots[0]);
        Assert.IsTrue(monitor.MoveEffectTo(last, 2));
        Assert.AreSame(last, monitor.EffectSlots[2]);
        monitor.EffectSlots[0].IsEnabled = false;
        monitor.EffectsBypassed = true;

        var setting = monitor.ToSetting();
        Assert.AreEqual(4, setting.EffectiveEffects.Count);
        Assert.IsTrue(setting.EffectsBypassed);
        Assert.IsFalse(setting.EffectiveEffects[0].IsEnabled);

        monitor.Profile = AudioInputProfileKind.Bass;
        Assert.AreEqual(4, monitor.EffectSlots.Count, "Cambiar la etiqueta no debe borrar los VST3 elegidos.");
    }

    private static Vst3EffectReference Effect(string name) => new(
        $@"C:\VST3\{name}.vst3",
        name,
        Guid.NewGuid().ToString("N").ToUpperInvariant(),
        "Audio Module Class",
        name,
        "Vendor",
        "1",
        "3.7",
        "Fx");
}
