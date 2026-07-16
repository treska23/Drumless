using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public static class PlaylistMixService
{
    public static IReadOnlyList<PlaylistItem> BuildQueue(
        IEnumerable<Playlist> playlists,
        Playlist? fallbackPlaylist)
    {
        ArgumentNullException.ThrowIfNull(playlists);
        var snapshot = playlists.ToArray();
        var included = snapshot.Where(playlist => playlist.IsIncludedInMix).ToArray();
        IEnumerable<Playlist> sources = included.Length > 0
            ? included
            : fallbackPlaylist is null ? [] : [fallbackPlaylist];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new List<PlaylistItem>();
        foreach (var playlist in sources)
        {
            foreach (var item in playlist.Items)
            {
                if (seen.Add(item.MediaKey))
                {
                    queue.Add(item);
                }
            }
        }

        return queue;
    }
}
