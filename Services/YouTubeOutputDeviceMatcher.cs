using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public static class YouTubeOutputDeviceMatcher
{
    private static readonly string[] IgnoredTokens =
    [
        "audio", "asio", "directo", "direct", "driver", "output", "salida",
        "speaker", "speakers", "altavoz", "altavoces", "headphone", "headphones",
        "auriculares", "usb", "wasapi", "device"
    ];

    public static IReadOnlyList<string> BuildAliases(
        AudioOutputDeviceItem selected,
        IEnumerable<AudioOutputDeviceItem> available)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(available);

        var aliases = new List<string> { selected.Name };
        if (selected.IsAsio)
        {
            aliases.AddRange(available
                .Where(device => !device.IsAsio)
                .Select(device => (device.Name, Score: ScoreName(selected.Name, device.Name)))
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
                .Take(4)
                .Select(candidate => candidate.Name));
        }

        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    public static int ScoreName(string left, string right)
    {
        var first = Tokenize(left);
        var second = Tokenize(right);
        if (first.Count == 0 || second.Count == 0)
        {
            return 0;
        }

        return first.Intersect(second).Count() * 100 / Math.Max(first.Count, second.Count);
    }

    private static HashSet<string> Tokenize(string value) => value
        .ToLowerInvariant()
        .Split([' ', '-', '_', '(', ')', '[', ']', '.', ',', ':'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(token => token.Length > 1 &&
                        !IgnoredTokens.Contains(token, StringComparer.Ordinal))
        .ToHashSet(StringComparer.Ordinal);
}
