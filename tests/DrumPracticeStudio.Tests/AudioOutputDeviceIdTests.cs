using DrumPracticeStudio.Audio;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class AudioOutputDeviceIdTests
{
    [TestMethod]
    public void AsioId_RoundTripsDriverName()
    {
        var id = AudioOutputDeviceId.ForAsio("Focusrite USB ASIO");

        Assert.IsTrue(AudioOutputDeviceId.TryGetAsioDriverName(id, out var driverName));
        Assert.AreEqual("Focusrite USB ASIO", driverName);
    }

    [TestMethod]
    public void WasapiId_IsNotReportedAsAsio()
    {
        Assert.IsFalse(AudioOutputDeviceId.TryGetAsioDriverName("{wasapi-device}", out _));
    }
}
