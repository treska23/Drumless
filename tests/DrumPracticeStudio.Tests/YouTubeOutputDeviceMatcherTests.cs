using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class YouTubeOutputDeviceMatcherTests
{
    [TestMethod]
    public void BuildAliases_MapsAsioDriverToMatchingWasapiEndpoint()
    {
        var selected = new AudioOutputDeviceItem(
            "asio:Focusrite USB ASIO",
            "Focusrite USB ASIO",
            false,
            AudioOutputBackend.Asio);
        AudioOutputDeviceItem[] available =
        [
            selected,
            new("focusrite-wasapi", "Altavoces (Focusrite USB Audio)", false),
            new("television", "LG TV (NVIDIA High Definition Audio)", true)
        ];

        var aliases = YouTubeOutputDeviceMatcher.BuildAliases(selected, available);

        CollectionAssert.Contains(aliases.ToArray(), "Focusrite USB ASIO");
        CollectionAssert.Contains(aliases.ToArray(), "Altavoces (Focusrite USB Audio)");
        CollectionAssert.DoesNotContain(aliases.ToArray(), "LG TV (NVIDIA High Definition Audio)");
    }

    [TestMethod]
    public void BuildAliases_DoesNotAddUnrelatedWasapiDevice()
    {
        var selected = new AudioOutputDeviceItem(
            "asio:Focusrite USB ASIO",
            "Focusrite USB ASIO",
            false,
            AudioOutputBackend.Asio);
        AudioOutputDeviceItem[] available =
        [
            selected,
            new("television", "Samsung TV (NVIDIA High Definition Audio)", true)
        ];

        var aliases = YouTubeOutputDeviceMatcher.BuildAliases(selected, available);

        CollectionAssert.AreEqual(
            new[] { "Focusrite USB ASIO" },
            aliases.ToArray());
    }
}
