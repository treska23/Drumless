using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class ChordSheetParserTests
{
    [TestMethod]
    public void Parse_ChordProLine_PreservesChordAndLyricAlignmentAsSeparateLines()
    {
        var document = new ChordSheetParser().Parse(
            "[Verse 1]\n[Em]Hoy comienza otra [C]historia");

        Assert.AreEqual(3, document.Lines.Count);
        Assert.AreEqual(ChordSheetLineKind.Section, document.Lines[0].Kind);
        Assert.AreEqual(ChordSheetLineKind.Chords, document.Lines[1].Kind);
        Assert.AreEqual(ChordSheetLineKind.Lyrics, document.Lines[2].Kind);
        StringAssert.Contains(document.Lines[1].Text, "Em");
        StringAssert.Contains(document.Lines[1].Text, "C");
        Assert.AreEqual("Hoy comienza otra historia", document.Lines[2].Text);
    }

    [TestMethod]
    public void NormalizeExtractedText_RemovesOuterAndExcessiveBlankLines()
    {
        var result = ChordSheetParser.NormalizeExtractedText(
            "\n\nIntro\n\n\n\nC G\nTexto\n\n");

        Assert.AreEqual(
            $"Intro{Environment.NewLine}{Environment.NewLine}{Environment.NewLine}" +
            $"C G{Environment.NewLine}Texto",
            result);
    }
}
