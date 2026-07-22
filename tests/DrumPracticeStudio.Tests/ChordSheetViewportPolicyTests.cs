using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class ChordSheetViewportPolicyTests
{
    [TestMethod]
    public void ResolveAnchorLineId_UsesLatestReachedMarkerAndSupportsBacktracking()
    {
        var lines = new[]
        {
            Line("top", 0),
            Line("middle", 1),
            Line("lower", 2),
            Line("ending", 3)
        };
        var markers = new[]
        {
            Marker("a", 30d, "middle"),
            Marker("b", 60d, "lower"),
            Marker("c", 90d, "ending"),
            Marker("d", 150d, "middle")
        };

        Assert.AreEqual("top", ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 29.9d, markers));
        Assert.AreEqual("middle", ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 30d, markers));
        Assert.AreEqual("lower", ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 75d, markers));
        Assert.AreEqual("ending", ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 120d, markers));
        Assert.AreEqual("middle", ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 160d, markers));
        Assert.AreEqual("middle", ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 45d, markers));
    }

    [TestMethod]
    public void ResolveAnchorLineId_IgnoresInvalidMarkers()
    {
        var lines = new[] { Line("top", 0), Line("lower", 1) };
        var markers = new[]
        {
            Marker("negative", -1d, "lower"),
            Marker("missing", 10d, "not-a-line")
        };

        Assert.AreEqual("top", ChordSheetViewportPolicy.ResolveAnchorLineId(
            lines, 100d, markers));
    }

    [TestMethod]
    public void ResolveActiveMarker_ChangesAtEveryBoundaryAndAfterBacktracking()
    {
        var lines = new[] { Line("top", 0), Line("same", 1) };
        var markers = new[]
        {
            Marker("first", 10d, "same"),
            Marker("second", 20d, "same"),
            Marker("third", 30d, "top")
        };

        Assert.IsNull(ChordSheetViewportPolicy.ResolveActiveMarker(lines, 9d, markers));
        Assert.AreEqual("first", ChordSheetViewportPolicy.ResolveActiveMarker(
            lines, 10d, markers)?.Id);
        Assert.AreEqual("second", ChordSheetViewportPolicy.ResolveActiveMarker(
            lines, 25d, markers)?.Id);
        Assert.AreEqual("third", ChordSheetViewportPolicy.ResolveActiveMarker(
            lines, 35d, markers)?.Id);
        Assert.AreEqual("first", ChordSheetViewportPolicy.ResolveActiveMarker(
            lines, 15d, markers)?.Id);
    }

    [TestMethod]
    public void Normalize_MigratesLegacySingleMarker()
    {
        var document = ChordSheetDocument.Normalize(new ChordSheetDocument(
            "sheet",
            "Song",
            ChordSheetSourceKind.UserText,
            null,
            "Line",
            DateTimeOffset.UtcNow,
            0d,
            [Line("lower", 0)],
            42d,
            "lower"));

        Assert.AreEqual(1, document.ViewportMarkers?.Count);
        Assert.AreEqual(42d, document.ViewportMarkers?[0].Seconds);
        Assert.AreEqual("lower", document.ViewportMarkers?[0].LineId);
        Assert.IsNull(document.ViewSwitchSeconds);
        Assert.IsNull(document.ViewSwitchLineId);
    }

    [TestMethod]
    public void Normalize_PreservesRepeatedTargetLineAtDifferentTimes()
    {
        var document = ChordSheetDocument.Normalize(new ChordSheetDocument(
            "sheet",
            "Song",
            ChordSheetSourceKind.UserText,
            null,
            "Chorus",
            DateTimeOffset.UtcNow,
            0d,
            [Line("chorus", 0)],
            ViewportMarkers:
            [
                Marker("first", 30d, "chorus"),
                Marker("repeat", 120d, "chorus")
            ]));

        Assert.AreEqual(2, document.ViewportMarkers?.Count);
        Assert.AreEqual(30d, document.ViewportMarkers?[0].Seconds);
        Assert.AreEqual(120d, document.ViewportMarkers?[1].Seconds);
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

    private static ChordSheetViewportMarker Marker(
        string id,
        double seconds,
        string lineId) => new(id, seconds, lineId);
}
