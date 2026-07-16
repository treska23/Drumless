using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public static class StemMixPlan
{
    private static readonly (StemSelection Stem, string FileName, string Label)[] Available =
    [
        (StemSelection.Drums, "drums.wav", "batería"),
        (StemSelection.Bass, "bass.wav", "bajo"),
        (StemSelection.Vocals, "vocals.wav", "voz"),
        (StemSelection.Other, "other.wav", "guitarras y otros")
    ];

    public static IReadOnlyList<string> GetFileNames(StemSelection selection)
    {
        Validate(selection);
        return Available
            .Where(item => selection.HasFlag(item.Stem))
            .Select(item => item.FileName)
            .ToArray();
    }

    public static string Describe(StemSelection selection)
    {
        Validate(selection);
        if (selection == StemSelection.All)
        {
            return "todos los stems";
        }
        if (selection == StemSelection.Drumless)
        {
            return "sin batería";
        }

        return string.Join(" + ", Available
            .Where(item => selection.HasFlag(item.Stem))
            .Select(item => item.Label));
    }

    public static string FileSuffix(StemSelection selection) =>
        Sanitize(Describe(selection));

    public static void Validate(StemSelection selection)
    {
        if (selection == StemSelection.None || (selection & ~StemSelection.All) != 0)
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
        .Replace(' ', '-');
}
