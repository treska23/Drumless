using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public static class PlaylistEditor
{
    public static bool AddTrack(Playlist playlist, string trackId)
    {
        Validate(playlist, trackId);
        if (FindIndex(playlist, trackId) >= 0)
        {
            return false;
        }

        playlist.TrackIds.Add(trackId);
        return true;
    }

    public static bool RemoveTrack(Playlist playlist, string trackId)
    {
        Validate(playlist, trackId);
        var index = FindIndex(playlist, trackId);
        if (index < 0)
        {
            return false;
        }

        playlist.TrackIds.RemoveAt(index);
        return true;
    }

    public static bool MoveUp(Playlist playlist, string trackId)
    {
        Validate(playlist, trackId);
        var index = FindIndex(playlist, trackId);
        if (index <= 0)
        {
            return false;
        }

        playlist.TrackIds.Move(index, index - 1);
        return true;
    }

    public static bool MoveDown(Playlist playlist, string trackId)
    {
        Validate(playlist, trackId);
        var index = FindIndex(playlist, trackId);
        if (index < 0 || index >= playlist.TrackIds.Count - 1)
        {
            return false;
        }

        playlist.TrackIds.Move(index, index + 1);
        return true;
    }

    private static int FindIndex(Playlist playlist, string trackId)
    {
        for (var index = 0; index < playlist.TrackIds.Count; index++)
        {
            if (string.Equals(playlist.TrackIds[index], trackId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void Validate(Playlist playlist, string trackId)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(trackId);
    }
}
