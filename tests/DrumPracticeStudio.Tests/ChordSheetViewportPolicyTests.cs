using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class ChordSheetViewportPolicyTests
{
    [TestMethod]
    public void ResolveAnchorLineId_ChangesOnlyOnceAtConfiguredTime()
    {
        var lines = new[]
        {
            Line("top", 0),
            Line("middle", 1),
            Line("lower", 2)
        };

        Assert.AreEqual("top", ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 89.9d, 90d, "lower"));
        Assert.AreEqual("lower", ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 90d, 90d, "lower"));
        Assert.AreEqual("lower", ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 180d, 90d, "lower"));
    }

    [TestMethod]
    public void ResolveAnchorLineId_RequiresACompleteValidMarker()
    {
        var lines = new[] { Line("top", 0), Line("lower", 1) };

        Assert.IsNull(ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 100d, null, "lower"));
        Assert.IsNull(ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 100d, 90d, "missing"));
    }

    [TestMethod]
    [DataRow("75", 75d)]
    [DataRow("1:15", 75d)]
    [DataRow("1:02:03", 3723d)]
    public void TimestampParser_AcceptsSecondsMinutesAndHours(string text, double expected)
    {
        Assert.IsTrue(ChordSheetViewportPolicy.TryParseTimestamp(text, out var seconds));
        Assert.AreEqual(expected, seconds, 0.001d);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("1:75")]
    [DataRow("abc")]
    [DataRow("-2")]
    public void TimestampParser_RejectsInvalidValues(string text) =>
        Assert.IsFalse(ChordSheetViewportPolicy.TryParseTimestamp(text, out _));

    private static ChordSheetLine Line(string id, int order) => new(
        id,
        order,
        ChordSheetLineKind.Lyrics,
        id,
        null,
        1d);
}
