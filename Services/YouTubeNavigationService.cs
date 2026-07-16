namespace DrumPracticeStudio.Services;

public static class YouTubeNavigationService
{
    public static readonly Uri HomeUri = new("https://www.youtube.com/");

    public static Uri CreateSearchUri(string query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return new Uri(
            $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query.Trim())}");
    }

    public static bool IsYouTubeUri(Uri? uri) =>
        uri is not null &&
        uri.Scheme is "https" or "http" &&
        (string.Equals(uri.Host, "youtube.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(uri.Host, "youtu.be", StringComparison.OrdinalIgnoreCase));

    public static bool TryGetVideoId(Uri? uri, out string videoId)
    {
        videoId = string.Empty;
        if (!IsYouTubeUri(uri) || uri is null)
        {
            return false;
        }

        if (string.Equals(uri.Host, "youtu.be", StringComparison.OrdinalIgnoreCase))
        {
            videoId = uri.AbsolutePath.Trim('/').Split('/')[0];
            return IsValidVideoId(videoId);
        }

        if (uri.AbsolutePath.Equals("/watch", StringComparison.OrdinalIgnoreCase))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            videoId = query["v"] ?? string.Empty;
            return IsValidVideoId(videoId);
        }

        var segments = uri.AbsolutePath.Trim('/').Split('/');
        if (segments.Length >= 2 &&
            (segments[0].Equals("shorts", StringComparison.OrdinalIgnoreCase) ||
             segments[0].Equals("embed", StringComparison.OrdinalIgnoreCase)))
        {
            videoId = segments[1];
            return IsValidVideoId(videoId);
        }

        return false;
    }

    public static bool TryGetPlaylistId(Uri? uri, out string playlistId)
    {
        playlistId = string.Empty;
        if (!IsYouTubeUri(uri) || uri is null)
        {
            return false;
        }

        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        playlistId = query["list"] ?? string.Empty;
        return IsValidPlaylistId(playlistId);
    }

    public static bool TryGetNavigationUri(string text, out Uri uri)
    {
        uri = null!;
        return !string.IsNullOrWhiteSpace(text) &&
               Uri.TryCreate(text.Trim(), UriKind.Absolute, out var candidate) &&
               IsYouTubeUri(candidate) &&
               (uri = candidate) is not null;
    }

    public static Uri CreateWatchUri(string videoId)
    {
        if (!IsValidVideoId(videoId))
        {
            throw new ArgumentException("El identificador de vídeo no es válido.", nameof(videoId));
        }
        return new Uri($"https://www.youtube.com/watch?v={videoId}");
    }

    public static string CreateThumbnailUrl(string videoId) =>
        $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

    private static bool IsValidVideoId(string value) =>
        value.Length is >= 6 and <= 20 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');

    private static bool IsValidPlaylistId(string value) =>
        value.Length is >= 10 and <= 100 &&
        value.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
}
