namespace DrumPracticeStudio.Models;

public sealed record YouTubePlaylistEntry(
    string VideoId,
    string Title,
    string Url,
    string? ThumbnailUrl);

public sealed record YouTubePlaylistImportResult(
    int Discovered,
    int Added,
    int Duplicates,
    string PlaylistName);
