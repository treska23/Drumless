using System.Text;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class ProcessLoopbackCaptureProtocolTests
{
    [TestMethod]
    public void DecodeError_ReturnsOriginalDiagnostic()
    {
        const string diagnostic = "COMException: captura no disponible";
        var response = "ERROR:" + Convert.ToBase64String(
            Encoding.UTF8.GetBytes(diagnostic));

        Assert.AreEqual(
            diagnostic,
            ProcessLoopbackCaptureProtocol.DecodeError(response));
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("READY")]
    [DataRow("mensaje sin protocolo")]
    public void DecodeError_IgnoresNonErrorResponses(string? response)
    {
        Assert.IsNull(ProcessLoopbackCaptureProtocol.DecodeError(response));
    }
}
