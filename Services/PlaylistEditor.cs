using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public static class PlaylistEditor
{
    public static bool AddTrack(Playlist playlist, LocalTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return AddItem(playlist, new PlaylistItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = PlaylistItemKind.LocalTrack,
            TrackId = track.Id,
            Title = track.Title
        });
    }

    public static bool AddYouTube(
        Playlist playlist,
        string videoId,
        string url,
        string title,
        string? thumbnailUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return AddItem(playlist, new PlaylistItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Kind = PlaylistItemKind.YouTube,
            YouTubeVideoId = videoId,
            YouTubeUrl = url,
            Title = title.Trim(),
            ThumbnailUrl = thumbnailUrl
        });
    }

    public static int AddYouTubeRange(
        Playlist playlist,
        IEnumerable<YouTubePlaylistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(entries);
        var added = 0;
        foreach (var entry in entries)
        {
            if (entry is not null && AddYouTube(
                    playlist,
                    entry.VideoId,
                    entry.Url,
                    entry.Title,
                    entry.ThumbnailUrl))
            {
                added++;
            }
        }
        return added;
    }

    public static bool AddItem(Playlist playlist, PlaylistItem item)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(item);
        if (playlist.Items.Any(existing =>
                string.Equals(existing.MediaKey, item.MediaKey, StringComparison.Ordinal)))
        {
            return false;
        }

        playlist.Items.Add(item);
        return true;
    }

    public static bool RemoveItem(Playlist playlist, string itemId)
    {
        Validate(playlist, itemId);
        var index = FindIndex(playlist, itemId);
        if (index < 0)
        {
            return false;
        }

        playlist.Items.RemoveAt(index);
        return true;
    }

    public static bool MoveUp(Playlist playlist, string itemId)
    {
        Validate(playlist, itemId);
        var index = FindIndex(playlist, itemId);
        if (index <= 0)
        {
            return false;
        }

        playlist.Items.Move(index, index - 1);
        return true;
    }

    public static bool MoveDown(Playlist playlist, string itemId)
    {
        Validate(playlist, itemId);
        var index = FindIndex(playlist, itemId);
        if (index < 0 || index >= playlist.Items.Count - 1)
        {
            return false;
        }

        playlist.Items.Move(index, index + 1);
        return true;
    }

    public static bool MoveTo(Playlist playlist, string itemId, int targetIndex)
    {
        Validate(playlist, itemId);
        var currentIndex = FindIndex(playlist, itemId);
        if (currentIndex < 0 || playlist.Items.Count == 0)
        {
            return false;
        }

        var boundedTarget = Math.Clamp(targetIndex, 0, playlist.Items.Count - 1);
        if (currentIndex == boundedTarget)
        {
            return false;
        }

        playlist.Items.Move(currentIndex, boundedTarget);
        return true;
    }

    private static int FindIndex(Playlist playlist, string itemId)
    {
        for (var index = 0; index < playlist.Items.Count; index++)
        {
            if (string.Equals(playlist.Items[index].Id, itemId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void Validate(Playlist playlist, string itemId)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
    }
}
