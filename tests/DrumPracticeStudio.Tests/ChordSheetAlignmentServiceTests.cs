using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class ChordSheetAlignmentServiceTests
{
    [TestMethod]
    public void Align_MapsSectionHeadersToDetectedStructureAndPreservesManualAnchors()
    {
        var parser = new ChordSheetParser();
        var document = parser.Parse(
            "[Verse]\nC G\nFirst line\nSecond line\n[Chorus]\nF G\nChorus line");
        var manualLine = document.Lines[2] with
        {
            StartSeconds = 7.25d,
            Confidence = 1d
        };
        document = document with
        {
            Lines = document.Lines
                .Select(line => line.Id == manualLine.Id ? manualLine : line)
                .ToArray()
        };
        var structure = new SongStructureMap(
            DateTimeOffset.UtcNow,
            60d,
            0.8d,
            [
                new SongSection("a", 0d, 30d, "Sección A", 0.8d, "a"),
                new SongSection("b", 30d, 60d, "Sección B", 0.8d, "b")
            ]);

        var aligned = new ChordSheetAlignmentService().Align(document, structure, 60d);

        Assert.AreEqual(0d, aligned.Lines[0].StartSeconds);
        Assert.AreEqual(30d, aligned.Lines[4].StartSeconds);
        Assert.AreEqual(7.25d, aligned.Lines[2].StartSeconds);
        Assert.AreEqual(1d, aligned.Lines[2].Confidence);
    }
}
