using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    public Playlist CreatePlaylistForYouTubeImport(string sourceName)
    {
        var baseName = string.IsNullOrWhiteSpace(sourceName)
            ? "Playlist de YouTube"
            : sourceName.Trim();
        var name = MakeUniquePlaylistName(baseName);
        var playlist = new Playlist
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = name
        };

        AttachPlaylist(playlist);
        Playlists.Add(playlist);
        RebuildVisiblePlaylists();
        SelectedPlaylist = playlist;
        PlaylistNameDraft = playlist.Name;
        SaveTrackWorkspace();
        StatusMessage = $"Playlist creada: {playlist.Name}";
        return playlist;
    }

    public YouTubePlaylistImportResult ImportYouTubePlaylistInto(
        Playlist targetPlaylist,
        IReadOnlyList<YouTubePlaylistEntry> entries,
        string sourceName)
    {
        ArgumentNullException.ThrowIfNull(targetPlaylist);
        ArgumentNullException.ThrowIfNull(entries);

        if (!Playlists.Contains(targetPlaylist))
        {
            return new YouTubePlaylistImportResult(entries.Count, 0, entries.Count, sourceName);
        }

        var added = PlaylistEditor.AddYouTubeRange(targetPlaylist, entries);
        var importedIds = entries
            .Select(entry => entry.VideoId)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in targetPlaylist.Items.Where(item =>
                     item.Kind == PlaylistItemKind.YouTube &&
                     item.YouTubeVideoId is not null &&
                     importedIds.Contains(item.YouTubeVideoId)))
        {
            item.Tempo = _analysisDatabase.GetTempo(item.MediaKey);
        }

        if (ReferenceEquals(SelectedPlaylist, targetPlaylist))
        {
            RebuildPlaylistItems();
            OnPropertyChanged(nameof(SelectedPlaylistSummary));
        }

        ResetPlaylistPlaybackQueue(_playbackNavigator.CurrentTrackId);
        OnPropertyChanged(nameof(MixedPlaylistSummary));
        SaveTrackWorkspace();

        StatusMessage = added == 0
            ? $"La playlist de YouTube ya estaba incluida en {targetPlaylist.Name}"
            : $"{added} vídeos importados desde {sourceName} a {targetPlaylist.Name}";

        return new YouTubePlaylistImportResult(
            entries.Count,
            added,
            entries.Count - added,
            sourceName);
    }

    private string MakeUniquePlaylistName(string baseName)
    {
        if (!Playlists.Any(playlist =>
                string.Equals(playlist.Name, baseName, StringComparison.CurrentCultureIgnoreCase)))
        {
            return baseName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName} ({suffix})";
            if (!Playlists.Any(playlist =>
                    string.Equals(playlist.Name, candidate, StringComparison.CurrentCultureIgnoreCase)))
            {
                return candidate;
            }
        }
    }
}
