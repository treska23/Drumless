using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public enum LibraryTrackSortMode
{
    DateAddedNewest,
    DateAddedOldest,
    NameAscending,
    NameDescending
}

public sealed record LibraryTrackSortOption(
    LibraryTrackSortMode Mode,
    string Label);

public static class LibraryTrackQuery
{
    public static IReadOnlyList<LocalTrack> Apply(
        IEnumerable<LocalTrack> tracks,
        string? searchText,
        LibraryTrackSortMode sortMode)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var query = tracks;
        var normalizedSearch = searchText?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedSearch))
        {
            query = query.Where(track =>
                track.Title.Contains(normalizedSearch, StringComparison.CurrentCultureIgnoreCase) ||
                track.Path.Contains(normalizedSearch, StringComparison.CurrentCultureIgnoreCase) ||
                track.VariantLabel.Contains(normalizedSearch, StringComparison.CurrentCultureIgnoreCase));
        }

        return sortMode switch
        {
            LibraryTrackSortMode.DateAddedOldest => query
                .OrderBy(track => track.DateAddedUtc)
                .ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray(),
            LibraryTrackSortMode.NameAscending => query
                .OrderBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(track => track.DateAddedUtc)
                .ToArray(),
            LibraryTrackSortMode.NameDescending => query
                .OrderByDescending(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
                .ThenByDescending(track => track.DateAddedUtc)
                .ToArray(),
            _ => query
                .OrderByDescending(track => track.DateAddedUtc)
                .ThenBy(track => track.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToArray()
        };
    }
}
