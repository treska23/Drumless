using System.Text.RegularExpressions;

namespace DrumPracticeStudio.Services;

internal static class SongIdentityResolver
{
    public static bool TryResolve(
        string trackTitle,
        out string artist,
        out string songTitle)
    {
        var fileTitle = (trackTitle ?? string.Empty).Trim();
        var extension = Path.GetExtension(fileTitle);
        if (extension is not null &&
            (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".aiff", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".aif", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".m4a", StringComparison.OrdinalIgnoreCase) ||
             extension.Equals(".wma", StringComparison.OrdinalIgnoreCase)))
        {
            fileTitle = Path.GetFileNameWithoutExtension(fileTitle).Trim();
        }
        fileTitle = Regex.Replace(
            fileTitle,
            @"[\(\[]\s*(drumless|sin\s+bater[ií]a|backing\s+track|official\s+(audio|video)|lyrics?)\s*[\)\]]",
            string.Empty,
            RegexOptions.IgnoreCase).Trim();
        fileTitle = Regex.Replace(fileTitle, @"^\d{1,3}\s*[.\-_]+\s*", string.Empty).Trim();
        var parts = Regex.Split(fileTitle, @"\s+[-–—]\s+")
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .ToList();
        if (parts.Count >= 3 && Regex.IsMatch(parts[0], @"^\d{1,3}$"))
        {
            parts.RemoveAt(0);
        }
        if (parts.Count == 2 &&
            !Regex.IsMatch(parts[0], @"^\d{1,3}$") &&
            !IsGenericPart(parts[0]) &&
            !IsGenericPart(parts[1]))
        {
            artist = parts[0];
            songTitle = parts[1];
            return true;
        }

        artist = string.Empty;
        songTitle = parts.LastOrDefault() ?? fileTitle;
        return false;
    }

    private static bool IsGenericPart(string value) =>
        Regex.IsMatch(
            value,
            @"^(track|pista|audio|original|song|canci[oó]n|unknown|desconocid[oa])\s*\d*$",
            RegexOptions.IgnoreCase);
}
