using System.Globalization;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public static class ChordSheetViewportPolicy
{
    public static string? ResolveAnchorLineId(
        IReadOnlyList<ChordSheetLine> lines,
        double playbackSeconds,
        double? switchSeconds,
        string? switchLineId)
    {
        if (lines.Count == 0 ||
            switchSeconds is not { } threshold ||
            !double.IsFinite(threshold) ||
            threshold < 0d ||
            string.IsNullOrWhiteSpace(switchLineId) ||
            lines.All(line => !string.Equals(line.Id, switchLineId, StringComparison.Ordinal)))
        {
            return null;
        }

        var firstVisible = lines.FirstOrDefault(line => line.Kind != ChordSheetLineKind.Empty)
                           ?? lines[0];
        return Math.Max(0d, playbackSeconds) >= threshold
            ? switchLineId
            : firstVisible.Id;
    }

    public static bool TryParseTimestamp(string? text, out double seconds)
    {
        seconds = 0d;
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return false;
        }

        var parts = value.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 3 ||
            parts.Any(part => part.Length == 0))
        {
            return false;
        }

        double total = 0d;
        var components = new double[parts.Length];
        for (var index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(
                    parts[index],
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var component) ||
                !double.IsFinite(component) ||
                component < 0d)
            {
                return false;
            }
            components[index] = component;
            total = (total * 60d) + component;
        }

        if ((parts.Length >= 2 && components[^1] >= 60d) ||
            (parts.Length == 3 && components[1] >= 60d))
        {
            return false;
        }

        seconds = total;
        return true;
    }

    public static string FormatTimestamp(double? seconds)
    {
        if (seconds is not { } value || !double.IsFinite(value) || value < 0d)
        {
            return string.Empty;
        }
        var time = TimeSpan.FromSeconds(value);
        return time.TotalHours >= 1d
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
    }
}
