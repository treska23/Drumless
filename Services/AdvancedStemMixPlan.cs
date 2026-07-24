using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public static class AdvancedStemMixPlan
{
    private static readonly (AdvancedStemSelection Stem, string FileName, string Label)[] Available =
    [
        (AdvancedStemSelection.Drums, "drums.wav", "batería"),
        (AdvancedStemSelection.Bass, "bass.wav", "bajo"),
        (AdvancedStemSelection.LeadVocal, "lead-vocal.wav", "voz principal"),
        (AdvancedStemSelection.BackVocal, "back-vocal.wav", "coros"),
        (AdvancedStemSelection.LeadGuitar, "lead-guitar.wav", "guitarra solista"),
        (AdvancedStemSelection.RhythmGuitar, "rhythm-guitar.wav", "guitarra rítmica"),
        (AdvancedStemSelection.Piano, "piano.wav", "piano y teclados"),
        (AdvancedStemSelection.Other, "other.wav", "otros")
    ];

    public static IReadOnlyList<string> GetFileNames(AdvancedStemSelection selection)
    {
        Validate(selection);
        return Available
            .Where(item => selection.HasFlag(item.Stem))
            .Select(item => item.FileName)
            .ToArray();
    }

    public static string Describe(AdvancedStemSelection selection)
    {
        Validate(selection);
        if (selection == AdvancedStemSelection.All)
        {
            return "todos los stems avanzados";
        }

        return string.Join(" + ", Available
            .Where(item => selection.HasFlag(item.Stem))
            .Select(item => item.Label));
    }

    public static AdvancedStemSelection FromStandardSelection(StemSelection selection)
    {
        var advanced = AdvancedStemSelection.None;
        if (selection.HasFlag(StemSelection.Drums)) advanced |= AdvancedStemSelection.Drums;
        if (selection.HasFlag(StemSelection.Bass)) advanced |= AdvancedStemSelection.Bass;
        if (selection.HasFlag(StemSelection.Vocals)) advanced |= AdvancedStemSelection.LeadVocal | AdvancedStemSelection.BackVocal;
        if (selection.HasFlag(StemSelection.Guitar)) advanced |= AdvancedStemSelection.LeadGuitar | AdvancedStemSelection.RhythmGuitar;
        if (selection.HasFlag(StemSelection.Piano)) advanced |= AdvancedStemSelection.Piano;
        if (selection.HasFlag(StemSelection.Other)) advanced |= AdvancedStemSelection.Other;
        return advanced == AdvancedStemSelection.None ? AdvancedStemSelection.All : advanced;
    }

    public static bool RequiresAdvancedSplit(AdvancedStemSelection selection)
    {
        Validate(selection);
        var splitVocals = selection.HasFlag(AdvancedStemSelection.LeadVocal) ^
                          selection.HasFlag(AdvancedStemSelection.BackVocal);
        var splitGuitars = selection.HasFlag(AdvancedStemSelection.LeadGuitar) ^
                           selection.HasFlag(AdvancedStemSelection.RhythmGuitar);
        return splitVocals || splitGuitars;
    }

    public static string FileSuffix(AdvancedStemSelection selection) => Sanitize(Describe(selection));

    public static void Validate(AdvancedStemSelection selection)
    {
        if (selection == AdvancedStemSelection.None ||
            (selection & ~AdvancedStemSelection.All) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selection),
                "Selecciona al menos un stem válido para el archivo final.");
        }
    }

    private static string Sanitize(string value) => value
        .Replace('í', 'i')
        .Replace('ó', 'o')
        .Replace('á', 'a')
        .Replace('ú', 'u')
        .Replace('é', 'e')
        .Replace(' ', '-');
}
