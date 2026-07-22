using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed partial class SongEffectRecommendationService : IDisposable
{
    private const int MaximumCatalogEntries = 240;
    private readonly HttpClient _internet;
    private readonly HttpClient _ollama;
    private readonly bool _ownsClients;

    public SongEffectRecommendationService()
    {
        _internet = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _internet.DefaultRequestHeaders.UserAgent.ParseAdd(
            "DrumPracticeStudio/1.0 (+local song effect research)");
        _ollama = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromMinutes(2)
        };
        _ownsClients = true;
    }

    internal SongEffectRecommendationService(HttpClient internet, HttpClient ollama)
    {
        _internet = internet;
        _ollama = ollama;
    }

    public async Task<SongEffectProfile> RecommendAsync(
        SongEffectRecommendationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.MediaKey) ||
            string.IsNullOrWhiteSpace(request.TrackTitle) ||
            string.IsNullOrWhiteSpace(request.Artist) ||
            string.IsNullOrWhiteSpace(request.SongTitle))
        {
            throw new ArgumentException("La pista no tiene una identidad válida.", nameof(request));
        }

        var available = request.AvailableEffects
            .Where(effect =>
                !string.IsNullOrWhiteSpace(effect.CatalogId) &&
                !string.IsNullOrWhiteSpace(effect.Reference.Name))
            .GroupBy(effect => effect.CatalogId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(effect => effect.Reference.Vendor, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(effect => effect.Reference.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaximumCatalogEntries)
            .ToArray();
        if (available.Length == 0)
        {
            throw new InvalidOperationException(
                "No hay efectos VST3 instalados disponibles para crear una propuesta.");
        }

        var evidence = await SearchProductionEvidenceSafeAsync(
            $"{request.Artist} {request.SongTitle}",
            cancellationToken);
        var model = await GetFirstModelAsync(cancellationToken);
        var catalog = available.Select(effect => new
        {
            id = effect.CatalogId,
            name = effect.Reference.Name,
            vendor = effect.Reference.Vendor,
            type = effect.EffectType,
            categories = effect.Reference.SubCategories
        });
        var context = new
        {
            artist = request.Artist,
            song = request.SongTitle,
            bpm = request.Bpm,
            sections = request.SongSections,
            webEvidence = evidence,
            installedEffects = catalog
        };
        var prompt =
            "Diseña dos cadenas conservadoras para aproximar el sonido de la canción. " +
            "Input 1 es siempre guitarra mono e Input 2 es siempre voz mono. " +
            "Sólo puedes escoger identificadores incluidos en installedEffects y sólo efectos: " +
            "nunca instrumentos virtuales ni generadores de sonido. Máximo cuatro slots por cadena. " +
            "Mantén ganancia prudente y no inventes plugins ni fuentes. webEvidence son fragmentos " +
            "de búsqueda y pueden ser incompletos: señala la incertidumbre en summary. " +
            "Devuelve JSON estricto con este esquema: " +
            "{\"summary\":\"...\",\"guitar\":{\"description\":\"...\",\"slots\":[" +
            "{\"catalogId\":\"id exacto\",\"purpose\":\"...\",\"presetHint\":\"...\",\"mix\":0.0}]}," +
            "\"voice\":{\"description\":\"...\",\"slots\":[...]}}. " +
            "mix debe estar entre 0 y 1. No añadas texto fuera del JSON.\n" +
            JsonSerializer.Serialize(context);

        using var response = await _ollama.PostAsJsonAsync(
            "api/chat",
            new
            {
                model,
                stream = false,
                format = "json",
                options = new { temperature = 0.1 },
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = "Eres un técnico de mezcla prudente que trabaja sólo con el catálogo recibido."
                    },
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
            throw new InvalidDataException("Ollama no devolvió una propuesta de efectos.");
        }

        using var recommendation = JsonDocument.Parse(content);
        var root = recommendation.RootElement;
        var byId = available.ToDictionary(
            effect => effect.CatalogId,
            StringComparer.OrdinalIgnoreCase);
        var guitar = ParseChain(root, "guitar", 0, "Guitarra mono", byId);
        var voice = ParseChain(root, "voice", 1, "Voz mono", byId);
        if (guitar.Slots.Count == 0 && voice.Slots.Count == 0)
        {
            throw new InvalidDataException(
                "Ollama no eligió ningún plugin válido del catálogo instalado.");
        }

        return new SongEffectProfile(
            Guid.NewGuid().ToString("N"),
            request.MediaKey,
            "Original aproximado",
            request.TrackTitle,
            request.Artist,
            request.SongTitle,
            DateTimeOffset.UtcNow,
            model,
            GetString(root, "summary", "Propuesta local basada en los efectos instalados."),
            guitar,
            voice);
    }

    private async Task<string> GetFirstModelAsync(CancellationToken cancellationToken)
    {
        using var tagsResponse = await _ollama.GetAsync("api/tags", cancellationToken);
        tagsResponse.EnsureSuccessStatusCode();
        using var tagsDocument = JsonDocument.Parse(
            await tagsResponse.Content.ReadAsStreamAsync(cancellationToken));
        return tagsDocument.RootElement
            .GetProperty("models")
            .EnumerateArray()
            .Select(element => element.TryGetProperty("name", out var name) ? name.GetString() : null)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name))
            ?? throw new InvalidOperationException(
                "Ollama está activo pero no tiene modelos instalados.");
    }

    private async Task<IReadOnlyList<string>> SearchProductionEvidenceSafeAsync(
        string title,
        CancellationToken cancellationToken)
    {
        try
        {
            var query = $"{TempoSourceSearchService.NormalizeTitle(title)} guitar tone vocal production effects";
            var uri = new Uri(
                "https://html.duckduckgo.com/html/?q=" + Uri.EscapeDataString(query));
            var html = await _internet.GetStringAsync(uri, cancellationToken);
            return SearchResultRegex().Matches(html)
                .Cast<Match>()
                .Select(match =>
                    $"{CleanHtml(match.Groups["title"].Value)} — " +
                    CleanHtml(match.Groups["snippet"].Value))
                .Where(value => value.Length > 4)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            HttpRequestException or
            TaskCanceledException or
            InvalidOperationException)
        {
            return [];
        }
    }

    private static SongInputEffectChain ParseChain(
        JsonElement root,
        string propertyName,
        int channelIndex,
        string instrument,
        IReadOnlyDictionary<string, InstalledEffectDescriptor> available)
    {
        if (!root.TryGetProperty(propertyName, out var chain) ||
            chain.ValueKind != JsonValueKind.Object)
        {
            return new SongInputEffectChain(channelIndex, instrument, string.Empty, []);
        }

        var slots = new List<SongEffectSlotRecommendation>();
        var selectedCatalogIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (chain.TryGetProperty("slots", out var array) &&
            array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (slots.Count >= AudioEffectCatalog.MaximumSlots ||
                    item.ValueKind != JsonValueKind.Object)
                {
                    break;
                }
                var catalogId = GetString(item, "catalogId", string.Empty);
                if (!available.TryGetValue(catalogId, out var installed) ||
                    !selectedCatalogIds.Add(catalogId))
                {
                    continue;
                }
                var mix = item.TryGetProperty("mix", out var mixElement) &&
                          mixElement.TryGetDouble(out var parsedMix) &&
                          double.IsFinite(parsedMix)
                    ? Math.Clamp(parsedMix, 0d, 1d)
                    : 1d;
                slots.Add(new SongEffectSlotRecommendation(
                    installed.Reference,
                    installed.EffectType,
                    GetString(item, "purpose", installed.EffectType),
                    GetString(item, "presetHint", string.Empty),
                    mix));
            }
        }
        return new SongInputEffectChain(
            channelIndex,
            instrument,
            GetString(chain, "description", string.Empty),
            slots);
    }

    private static string GetString(JsonElement element, string property, string fallback) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : fallback;

    private static string CleanHtml(string value) =>
        WebUtility.HtmlDecode(HtmlTagRegex().Replace(value, " "))
            .Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Trim();

    public void Dispose()
    {
        if (_ownsClients)
        {
            _internet.Dispose();
            _ollama.Dispose();
        }
    }

    [GeneratedRegex(
        """<a[^>]*class="[^"]*result__a[^"]*"[^>]*>(?<title>.*?)</a>.*?<a[^>]*class="[^"]*result__snippet[^"]*"[^>]*>(?<snippet>.*?)</a>""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SearchResultRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();
}
