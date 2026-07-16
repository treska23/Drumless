using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public static class PlaylistMixService
{
    public static IReadOnlyList<string> BuildQueue(
        IEnumerable<Playlist> playlists,
        Playlist? fallbackPlaylist)
    {
        ArgumentNullException.ThrowIfNull(playlists);

        var playlistSnapshot = playlists.ToArray();
        var included = playlistSnapshot
            .Where(playlist => playlist.IsIncludedInMix)
            .ToArray();
        IEnumerable<Playlist> sources = included.Length > 0
            ? included
            : fallbackPlaylist is null
                ? []
                : [fallbackPlaylist];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new List<string>();
        foreach (var playlist in sources)
        {
            foreach (var trackId in playlist.TrackIds)
            {
                if (!string.IsNullOrWhiteSpace(trackId) && seen.Add(trackId))
                {
                    queue.Add(trackId);
                }
            }
        }

        return queue;
    }
}
