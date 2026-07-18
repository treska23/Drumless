using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed partial class TempoSourceSearchService : IDisposable
{
    private readonly HttpClient _internet;
    private readonly HttpClient _ollama;
    private readonly bool _ownsClients;

    public TempoSourceSearchService()
    {
        _internet = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _internet.DefaultRequestHeaders.UserAgent.ParseAdd(
            "DrumPracticeStudio/1.0 (+local tempo source lookup)");
        _ollama = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromSeconds(45)
        };
        _ownsClients = true;
    }

    internal TempoSourceSearchService(HttpClient internet, HttpClient ollama)
    {
        _internet = internet;
        _ollama = ollama;
    }

    public async Task<IReadOnlyList<TempoSourceCandidate>> SearchAsync(
        string mediaTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaTitle);
        var query = $"{NormalizeTitle(mediaTitle)} BPM tempo";
        var uri = new Uri(
            "https://html.duckduckgo.com/html/?q=" +
            Uri.EscapeDataString(query));
        var html = await _internet.GetStringAsync(uri, cancellationToken);
        var results = new List<TempoSourceCandidate>();
        foreach (Match match in SearchResultRegex().Matches(html))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var title = CleanHtml(match.Groups["title"].Value);
            var evidence = CleanHtml(match.Groups["snippet"].Value);
            var sourceUrl = ResolveResultUrl(
                WebUtility.HtmlDecode(match.Groups["url"].Value));
            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(evidence) ||
                sourceUrl is null)
            {
                continue;
            }

            var bpmMatches = BpmRegex().Matches($"{title} {evidence}");
            foreach (Match bpmMatch in bpmMatches)
            {
                var numeric = bpmMatch.Groups["bpm"].Value.Replace(',', '.');
                if (!double.TryParse(
                        numeric,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var bpm) ||
                    bpm is < 40d or > 240d)
                {
                    continue;
                }

                var sourceName = Uri.TryCreate(sourceUrl, UriKind.Absolute, out var sourceUri)
                    ? sourceUri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase)
                    : "Fuente web";
                results.Add(new TempoSourceCandidate(
                    Guid.NewGuid().ToString("N"),
                    Math.Round(bpm, 2),
                    title,
                    sourceName,
                    sourceUrl,
                    evidence,
                    Confidence: 0.72d));
            }
        }

        return results
            .GroupBy(
                candidate => $"{candidate.SourceUrl}|{candidate.Bpm:0.##}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(12)
            .ToArray();
    }

    public async Task<IReadOnlyList<TempoSourceCandidate>> ContrastWithOllamaAsync(
        IReadOnlyList<TempoSourceCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return candidates;
        }

        using var tagsResponse = await _ollama.GetAsync("api/tags", cancellationToken);
        tagsResponse.EnsureSuccessStatusCode();
        using var tagsDocument = JsonDocument.Parse(
            await tagsResponse.Content.ReadAsStreamAsync(cancellationToken));
        var model = tagsDocument.RootElement
            .GetProperty("models")
            .EnumerateArray()
            .Select(element => element.TryGetProperty("name", out var name) ? name.GetString() : null)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? throw new InvalidOperationException("Ollama está activo pero no tiene modelos instalados.");

        var evidence = candidates.Select(candidate => new
        {
            candidate.Id,
            candidate.Bpm,
            candidate.Title,
            candidate.SourceName,
            candidate.SourceUrl,
            candidate.Evidence
        });
        var prompt =
            "Contrasta únicamente estos candidatos de tempo ya obtenidos de fuentes web. " +
            "No inventes BPM, fuentes ni identificadores. Devuelve JSON estricto con " +
            "{\"ranking\":[{\"id\":\"id existente\",\"assessment\":\"explicación breve\"}]} " +
            "ordenado del más fiable al menos fiable. Detecta posibles errores de medio/doble tempo.\n" +
            JsonSerializer.Serialize(evidence);
        using var response = await _ollama.PostAsJsonAsync(
            "api/chat",
            new
            {
                model,
                stream = false,
                format = "json",
                messages = new[]
                {
                    new { role = "system", content = "Eres un verificador conservador de fuentes musicales." },
                    new { role = "user", content = prompt }
                }
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        using var responseDocument = JsonDocument.Parse(
            await response.Content.ReadAsStreamAsync(cancellationToken));
        var content = responseDocument.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidDataException("Ollama no devolvió una evaluación.");
        }

        using var rankingDocument = JsonDocument.Parse(content);
        var assessments = rankingDocument.RootElement
            .GetProperty("ranking")
            .EnumerateArray()
            .Select((item, index) => new
            {
                Id = item.TryGetProperty("id", out var id) ? id.GetString() : null,
                Assessment = item.TryGetProperty("assessment", out var assessment)
                    ? assessment.GetString()
                    : null,
                Index = index
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id!, StringComparer.Ordinal);
        return candidates
            .Select((candidate, originalIndex) => new
            {
                Candidate = assessments.TryGetValue(candidate.Id, out var assessment)
                    ? candidate with { OllamaAssessment = assessment.Assessment }
                    : candidate,
                Rank = assessments.TryGetValue(candidate.Id, out assessment)
                    ? assessment.Index
                    : int.MaxValue,
                OriginalIndex = originalIndex
            })
            .OrderBy(item => item.Rank)
            .ThenBy(item => item.OriginalIndex)
            .Select(item => item.Candidate)
            .ToArray();
    }

    public void Dispose()
    {
        if (_ownsClients)
        {
            _internet.Dispose();
            _ollama.Dispose();
        }
    }

    internal static string NormalizeTitle(string title)
    {
        var withoutExtension = Path.GetFileNameWithoutExtension(title);
        var cleaned = Regex.Replace(
                withoutExtension,
                @"\b(drumless|backing\s*track|official\s*(audio|video)|lyrics?)\b",
                " ",
                RegexOptions.IgnoreCase)
            .Replace('_', ' ')
            .Replace('-', ' ');
        cleaned = Regex.Replace(cleaned, @"[()[\]{}]", " ");
        return Regex.Replace(cleaned, @"\s+", " ").Trim();
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
        @"(?<!\d)(?<bpm>\d{2,3}(?:[.,]\d{1,2})?)\s*(?:BPM|beats?\s+per\s+minute)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex BpmRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
