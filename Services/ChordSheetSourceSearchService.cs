using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed partial class ChordSheetSourceSearchService : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public ChordSheetSourceSearchService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "DrumPracticeStudio/1.0 (+local chord sheet source lookup)");
        _ownsClient = true;
    }

    internal ChordSheetSourceSearchService(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<ChordSheetSourceCandidate>> SearchAsync(
        string mediaTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaTitle);
        var normalizedTitle = TempoSourceSearchService.NormalizeTitle(mediaTitle);
        var query = $"{normalizedTitle} letra acordes chords";
        var uri = new Uri(
            "https://html.duckduckgo.com/html/?q=" +
            Uri.EscapeDataString(query));
        var html = await _http.GetStringAsync(uri, cancellationToken);
        var candidates = new List<ChordSheetSourceCandidate>();
        foreach (Match match in SearchResultRegex().Matches(html))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = CleanHtml(match.Groups["title"].Value);
            var evidence = CleanHtml(match.Groups["snippet"].Value);
            var sourceUrl = ResolveResultUrl(
                WebUtility.HtmlDecode(match.Groups["url"].Value));
            if (string.IsNullOrWhiteSpace(title) || sourceUrl is null ||
                !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri))
            {
                continue;
            }

            var searchable = $"{title} {evidence} {sourceUri.Host}";
            var relevance = KeywordRegex().Matches(searchable).Count;
            if (relevance == 0)
            {
                continue;
            }
            var sourceName = sourceUri.Host.Replace(
                "www.",
                string.Empty,
                StringComparison.OrdinalIgnoreCase);
            candidates.Add(new ChordSheetSourceCandidate(
                Guid.NewGuid().ToString("N"),
                title,
                sourceName,
                sourceUri.AbsoluteUri,
                evidence,
                Math.Clamp(0.48d + relevance * 0.08d, 0d, 0.88d)));
        }

        return candidates
            .GroupBy(candidate => candidate.SourceUrl, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(candidate => candidate.Confidence)
            .Take(12)
            .ToArray();
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    private static string CleanHtml(string value) =>
        WebUtility.HtmlDecode(HtmlTagRegex().Replace(value, " "))
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("  ", " ", StringComparison.Ordinal)
            .Trim();

    private static string? ResolveResultUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return null;
        }
        if (uri.Host.EndsWith("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var redirected = query["uddg"];
            if (Uri.TryCreate(redirected, UriKind.Absolute, out var target) &&
                target.Scheme is "http" or "https")
            {
                return target.AbsoluteUri;
            }
        }
        return uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
    }

    [GeneratedRegex(
        """<a[^>]*class="[^"]*result__a[^"]*"[^>]*href="(?<url>[^"]+)"[^>]*>(?<title>.*?)</a>.*?<a[^>]*class="[^"]*result__snippet[^"]*"[^>]*>(?<snippet>.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SearchResultRegex();

    [GeneratedRegex(
        @"\b(?:acordes?|chords?|cifra|tablatura|tabs?|letra|lyrics?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex KeywordRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
