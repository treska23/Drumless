using System.Globalization;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public static class ChordSheetViewportPolicy
{
    public static string? ResolveAnchorLineId(
        IReadOnlyList<ChordSheetLine> lines,
        double playbackSeconds,
        IReadOnlyList<ChordSheetViewportMarker>? markers)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        var firstVisible = lines.FirstOrDefault(line => line.Kind != ChordSheetLineKind.Empty)
                           ?? lines[0];
        var lineIds = lines.Select(line => line.Id).ToHashSet(StringComparer.Ordinal);
        var position = Math.Max(0d, playbackSeconds);
        var activeMarker = (markers ?? [])
            .Where(marker =>
                marker is not null &&
                double.IsFinite(marker.Seconds) &&
                marker.Seconds >= 0d &&
                marker.Seconds <= position &&
                !string.IsNullOrWhiteSpace(marker.LineId) &&
                lineIds.Contains(marker.LineId))
            .OrderBy(marker => marker.Seconds)
            .LastOrDefault();
        return activeMarker?.LineId ?? firstVisible.Id;
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
