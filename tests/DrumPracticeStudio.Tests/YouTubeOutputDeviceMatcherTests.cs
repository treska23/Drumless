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

    [TestMethod]
    public void RecordingResolver_UsesExactWasapiEndpoint()
    {
        var selected = new AudioOutputDeviceItem(
            "focusrite-wasapi",
            "Altavoces (Focusrite USB Audio)",
            false);
        AudioOutputDeviceItem[] available =
        [
            selected,
            new("television", "Samsung TV (NVIDIA High Definition Audio)", true)
        ];

        var candidates = RecordingOutputEndpointResolver.ResolveCandidates(selected, available);

        CollectionAssert.AreEqual(new[] { selected }, candidates.ToArray());
    }

    [TestMethod]
    public void RecordingResolver_ForAsioKeepsMatchingWasapiAndRejectsTv()
    {
        var selected = new AudioOutputDeviceItem(
            "asio:Focusrite USB ASIO",
            "Focusrite USB ASIO",
            false,
            AudioOutputBackend.Asio);
        var focusrite = new AudioOutputDeviceItem(
            "focusrite-wasapi",
            "Altavoces (Focusrite USB Audio)",
            false);
        AudioOutputDeviceItem[] available =
        [
            selected,
            focusrite,
            new("television", "LG TV (NVIDIA High Definition Audio)", true)
        ];

        var candidates = RecordingOutputEndpointResolver.ResolveCandidates(selected, available);

        CollectionAssert.Contains(candidates.ToArray(), focusrite);
        Assert.IsFalse(candidates.Any(candidate => candidate.Id == "television"));
    }
}
