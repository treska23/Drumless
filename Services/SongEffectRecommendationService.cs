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
        var promptCatalog = SelectPromptCatalog(available);
        var catalog = promptCatalog.Select(effect => new
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
            "Copia literalmente el campo id en catalogId; no escribas el nombre en catalogId. " +
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
        var guitar = ParseChain(root, "guitar", 0, "Guitarra mono", byId, available);
        var voice = ParseChain(root, "voice", 1, "Voz mono", byId, available);
        var usedFallback = false;
        if (guitar.Slots.Count == 0)
        {
            guitar = CreateFallbackChain(0, "Guitarra mono", available);
            usedFallback = guitar.Slots.Count > 0;
        }
        if (voice.Slots.Count == 0)
        {
            voice = CreateFallbackChain(1, "Voz mono", available);
            usedFallback = usedFallback || voice.Slots.Count > 0;
        }
        if (guitar.Slots.Count == 0 && voice.Slots.Count == 0)
        {
            throw new InvalidDataException(
                "El catálogo no contiene efectos compatibles para guitarra o voz.");
        }

        var summary = GetString(
            root,
            "summary",
            "Propuesta local basada en los efectos instalados.");
        if (usedFallback)
        {
            summary += " La respuesta de Ollama no identificó todos los plugins por su código; " +
                       "los huecos se completaron de forma conservadora con efectos instalados.";
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
            summary,
            guitar,
            voice);
    }

    public async Task<SongEffectProfile> TuneParametersAsync(
        SongEffectProfile profile,
        IReadOnlyDictionary<string, IReadOnlyList<Vst3ParameterDescriptor>> parameterCatalog,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(parameterCatalog);
        var guitarCatalog = BuildTuningCatalog(profile.Guitar, "g", parameterCatalog);
        var voiceCatalog = BuildTuningCatalog(profile.Voice, "v", parameterCatalog);
        if (!guitarCatalog.Any(slot => slot.Parameters.Count > 0) ||
            !voiceCatalog.Any(slot => slot.Parameters.Count > 0))
        {
            throw new InvalidDataException(
                "Los plugins elegidos no exponen parámetros configurables para los dos inputs.");
        }

        var context = new
        {
            artist = profile.Artist,
            song = profile.SongTitle,
            guitar = guitarCatalog.Select(ToPromptSlot),
            voice = voiceCatalog.Select(ToPromptSlot)
        };
        var prompt =
            "Configura los parámetros reales de estos efectos VST3 para aproximar de forma " +
            "prudente el sonido de la canción. Input 1 es guitarra mono e Input 2 voz mono. " +
            "Usa únicamente slotId e ids de parámetros incluidos. normalized debe estar entre " +
            "0 y 1. Modifica sólo parámetros necesarios; evita subidas de nivel, realimentación " +
            "y valores extremos. Cada plugin que tenga parámetros debe recibir al menos un ajuste. " +
            "Devuelve JSON estricto: {\"guitar\":[{\"slotId\":\"g0\",\"parameters\":[" +
            "{\"id\":1,\"normalized\":0.5,\"reason\":\"...\"}]}],\"voice\":[...]}. " +
            "No añadas texto fuera del JSON.\n" + JsonSerializer.Serialize(context);

        using var response = await _ollama.PostAsJsonAsync(
            "api/chat",
            new
            {
                model = profile.OllamaModel,
                stream = false,
                format = "json",
                options = new { temperature = 0.05 },
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = "Eres un técnico de mezcla conservador. Configuras sólo parámetros VST3 enumerados."
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
            throw new InvalidDataException("Ollama no devolvió ajustes para los plugins.");
        }

        using var tuningDocument = JsonDocument.Parse(content);
        var tunedGuitar = ApplyParameterTuning(
            profile.Guitar,
            "guitar",
            guitarCatalog,
            tuningDocument.RootElement);
        var tunedVoice = ApplyParameterTuning(
            profile.Voice,
            "voice",
            voiceCatalog,
            tuningDocument.RootElement);
        if (tunedGuitar.Slots.Count == 0 || tunedVoice.Slots.Count == 0)
        {
            throw new InvalidDataException(
                "Ollama no configuró una cadena utilizable para los dos inputs.");
        }

        return profile with
        {
            Summary = profile.Summary +
                      " Los parámetros internos de los plugins se adaptaron para esta canción.",
            Guitar = tunedGuitar,
            Voice = tunedVoice
        };
    }

    private static IReadOnlyList<TuningSlot> BuildTuningCatalog(
        SongInputEffectChain chain,
        string prefix,
        IReadOnlyDictionary<string, IReadOnlyList<Vst3ParameterDescriptor>> parameterCatalog) =>
        chain.Slots.Select((slot, index) =>
        {
            var key = Vst3EffectItem.GetCatalogId(
                slot.Effect.ModulePath,
                slot.Effect.ClassId);
            parameterCatalog.TryGetValue(key, out var parameters);
            return new TuningSlot(
                $"{prefix}{index}",
                index,
                slot,
                SelectTunableParameters(parameters ?? [], slot.Purpose));
        }).ToArray();

    private static object ToPromptSlot(TuningSlot slot) => new
    {
        slotId = slot.SlotId,
        plugin = slot.Slot.Effect.Name,
        vendor = slot.Slot.Effect.Vendor,
        purpose = slot.Slot.Purpose,
        presetBase = string.IsNullOrWhiteSpace(slot.Slot.Effect.PresetPath)
            ? null
            : Path.GetFileNameWithoutExtension(slot.Slot.Effect.PresetPath),
        parameters = slot.Parameters.Select(parameter => new
        {
            id = parameter.Id,
            title = parameter.Title,
            shortTitle = parameter.ShortTitle,
            units = parameter.Units,
            steps = parameter.StepCount,
            defaultNormalized = parameter.DefaultNormalizedValue,
            defaultDisplay = parameter.DefaultDisplayValue,
            currentNormalized = parameter.CurrentNormalizedValue,
            currentDisplay = parameter.CurrentDisplayValue
        })
    };

    private static IReadOnlyList<Vst3ParameterDescriptor> SelectTunableParameters(
        IReadOnlyList<Vst3ParameterDescriptor> parameters,
        string purpose) => parameters
        .Where(parameter => !IsUnsafeLevelParameter(parameter.Title))
        .OrderByDescending(parameter => ParameterRelevanceScore(parameter, purpose))
        .ThenBy(parameter => parameter.Title, StringComparer.CurrentCultureIgnoreCase)
        .Take(40)
        .ToArray();

    private static int ParameterRelevanceScore(
        Vst3ParameterDescriptor parameter,
        string purpose)
    {
        var title = $" {NormalizePluginText(parameter.Title)} ";
        var purposeTerms = NormalizePluginText(purpose)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var score = purposeTerms.Count(term =>
            term.Length >= 4 && title.Contains(term, StringComparison.Ordinal)) * 8;
        score += ContainsAny(
            title,
            " drive ", " tone ", " threshold ", " ratio ", " attack ", " release ",
            " frequency ", " freq ", " band ", " mix ", " wet ", " dry ", " room ",
            " decay ", " time ", " feedback ", " depth ", " presence ", " bass ",
            " treble ", " deess ", " reduction ") ? 12 : 0;
        return score;
    }

    private static bool IsUnsafeLevelParameter(string title)
    {
        var normalized = $" {NormalizePluginText(title)} ";
        return ContainsAny(
            normalized,
            " output ", " master ", " makeup ", " make up ", " input gain ",
            " output gain ", " volume ", " level ", " vol ", " trim ", " ceiling ",
            " bypass ", " enable ", " on off ");
    }

    private static SongInputEffectChain ApplyParameterTuning(
        SongInputEffectChain chain,
        string propertyName,
        IReadOnlyList<TuningSlot> catalog,
        JsonElement root)
    {
        TryGetPropertyIgnoreCase(root, propertyName, out var responseChain);
        if (responseChain.ValueKind == JsonValueKind.Object &&
            TryGetPropertyIgnoreCase(responseChain, "slots", out var nested))
        {
            responseChain = nested;
        }
        var responseSlots = responseChain.ValueKind == JsonValueKind.Array
            ? responseChain.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object).ToArray()
            : [];
        var tuned = new List<SongEffectSlotRecommendation>();
        foreach (var slot in catalog)
        {
            var responseSlot = responseSlots.FirstOrDefault(item =>
                string.Equals(
                    GetString(item, "slotId", string.Empty),
                    slot.SlotId,
                    StringComparison.OrdinalIgnoreCase));
            if (responseSlot.ValueKind == JsonValueKind.Undefined &&
                slot.Index < responseSlots.Length)
            {
                responseSlot = responseSlots[slot.Index];
            }
            var settings = ParseParameterSettings(responseSlot, slot.Parameters);
            if (settings.Count == 0)
            {
                continue;
            }
            tuned.Add(slot.Slot with
            {
                Effect = slot.Slot.Effect with { ParameterSettings = settings }
            });
        }
        return chain with { Slots = tuned };
    }

    private static IReadOnlyList<Vst3ParameterSetting> ParseParameterSettings(
        JsonElement slot,
        IReadOnlyList<Vst3ParameterDescriptor> available)
    {
        if (slot.ValueKind != JsonValueKind.Object ||
            !TryGetPropertyIgnoreCase(slot, "parameters", out var parameters) ||
            parameters.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var byId = available.ToDictionary(parameter => parameter.Id);
        var selected = new Dictionary<uint, Vst3ParameterSetting>();
        foreach (var item in parameters.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetParameterId(item, available, out var id) ||
                !byId.TryGetValue(id, out var descriptor) ||
                !TryGetDouble(item, out var value, "normalized", "normalizedValue", "value"))
            {
                continue;
            }
            selected[id] = new Vst3ParameterSetting(
                id,
                descriptor.Title,
                Math.Clamp(value, 0.02d, 0.98d),
                GetString(item, "reason", string.Empty));
            if (selected.Count >= 24)
            {
                break;
            }
        }
        return selected.Values.ToArray();
    }

    private static bool TryGetParameterId(
        JsonElement item,
        IReadOnlyList<Vst3ParameterDescriptor> available,
        out uint id)
    {
        foreach (var propertyName in new[] { "id", "parameterId" })
        {
            if (!TryGetPropertyIgnoreCase(item, propertyName, out var idElement))
            {
                continue;
            }
            if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetUInt32(out id))
            {
                return true;
            }
            if (idElement.ValueKind == JsonValueKind.String &&
                uint.TryParse(idElement.GetString(), out id))
            {
                return true;
            }
        }
        var title = GetString(item, "title", GetString(item, "name", string.Empty));
        var descriptor = available.FirstOrDefault(parameter =>
            string.Equals(parameter.Title, title, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parameter.ShortTitle, title, StringComparison.OrdinalIgnoreCase));
        id = descriptor?.Id ?? 0;
        return descriptor is not null;
    }

    private static bool TryGetDouble(
        JsonElement item,
        out double value,
        params string[] properties)
    {
        foreach (var property in properties)
        {
            if (!TryGetPropertyIgnoreCase(item, property, out var element))
            {
                continue;
            }
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value) &&
                double.IsFinite(value))
            {
                return true;
            }
            if (element.ValueKind == JsonValueKind.String &&
                double.TryParse(
                    element.GetString(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out value) && double.IsFinite(value))
            {
                return true;
            }
        }
        value = 0d;
        return false;
    }

    private sealed record TuningSlot(
        string SlotId,
        int Index,
        SongEffectSlotRecommendation Slot,
        IReadOnlyList<Vst3ParameterDescriptor> Parameters);

    private static IReadOnlyList<InstalledEffectDescriptor> SelectPromptCatalog(
        IReadOnlyList<InstalledEffectDescriptor> available) => available
        .OrderByDescending(CatalogSuitabilityScore)
        .ThenBy(effect => effect.Reference.Vendor, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(effect => effect.Reference.Name, StringComparer.CurrentCultureIgnoreCase)
        .Take(MaximumCatalogEntries)
        .ToArray();

    private static int CatalogSuitabilityScore(InstalledEffectDescriptor effect)
    {
        var text = $" {NormalizePluginText(
            $"{effect.EffectType} {effect.Reference.Name} {effect.Reference.SubCategories}")} ";
        var score = 0;
        score += ContainsAny(text, "amp simulator", "amplifier", "guitar rig", "gtr amp") ? 100 : 0;
        score += ContainsAny(text, "distortion", "overdrive", "saturation") ? 85 : 0;
        score += ContainsAny(text, "dynamics", "compressor", "vocal", "voice", "deesser") ? 75 : 0;
        score += ContainsAny(text, "channel strip", "equalizer", " eq ") ? 65 : 0;
        score += ContainsAny(text, "reverb", "delay", "modulation") ? 45 : 0;
        score += effect.Reference.Name.Contains("Mono", StringComparison.OrdinalIgnoreCase) ? 20 : 0;
        score -= effect.Reference.Name.Contains("Surround", StringComparison.OrdinalIgnoreCase) ? 100 : 0;
        return score;
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
        IReadOnlyDictionary<string, InstalledEffectDescriptor> byId,
        IReadOnlyList<InstalledEffectDescriptor> available)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var chain) ||
            chain.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            return new SongInputEffectChain(channelIndex, instrument, string.Empty, []);
        }

        var slots = new List<SongEffectSlotRecommendation>();
        var selectedCatalogIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasArray = chain.ValueKind == JsonValueKind.Array;
        var array = chain;
        if (!hasArray && TryGetPropertyIgnoreCase(chain, "slots", out var nestedSlots))
        {
            array = nestedSlots;
            hasArray = array.ValueKind == JsonValueKind.Array;
        }
        if (hasArray)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (slots.Count >= AudioEffectCatalog.MaximumSlots ||
                    item.ValueKind != JsonValueKind.Object)
                {
                    break;
                }
                var installed = ResolveInstalledEffect(item, byId, available);
                if (installed is null ||
                    !selectedCatalogIds.Add(installed.CatalogId))
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
            chain.ValueKind == JsonValueKind.Object
                ? GetString(chain, "description", string.Empty)
                : string.Empty,
            slots);
    }

    private static InstalledEffectDescriptor? ResolveInstalledEffect(
        JsonElement item,
        IReadOnlyDictionary<string, InstalledEffectDescriptor> byId,
        IReadOnlyList<InstalledEffectDescriptor> available)
    {
        var references = new[]
        {
            GetString(item, "catalogId", string.Empty),
            GetString(item, "id", string.Empty),
            GetString(item, "pluginId", string.Empty),
            GetString(item, "pluginName", string.Empty),
            GetString(item, "name", string.Empty),
            GetString(item, "plugin", string.Empty),
            TryGetPropertyIgnoreCase(item, "plugin", out var plugin) &&
            plugin.ValueKind == JsonValueKind.Object
                ? GetString(plugin, "name", GetString(plugin, "id", string.Empty))
                : string.Empty
        }.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();

        foreach (var reference in references)
        {
            if (byId.TryGetValue(reference, out var exactId))
            {
                return exactId;
            }
        }

        foreach (var reference in references)
        {
            var normalized = NormalizePluginText(reference);
            var exactName = available.FirstOrDefault(effect =>
                string.Equals(
                    NormalizePluginText(effect.Reference.Name),
                    normalized,
                    StringComparison.Ordinal) ||
                string.Equals(
                    NormalizePluginText($"{effect.Reference.Name} {effect.Reference.Vendor}"),
                    normalized,
                    StringComparison.Ordinal));
            if (exactName is not null)
            {
                return exactName;
            }

            var containedName = normalized.Length < 4
                ? null
                : available
                .Where(effect =>
                {
                    var name = NormalizePluginText(effect.Reference.Name);
                    return name.Length >= 5 &&
                           (normalized.Contains(name, StringComparison.Ordinal) ||
                            name.Contains(normalized, StringComparison.Ordinal));
                })
                .OrderByDescending(effect =>
                    effect.Reference.Name.Contains("Mono", StringComparison.OrdinalIgnoreCase))
                .ThenBy(effect => effect.Reference.Name.Length)
                .FirstOrDefault();
            if (containedName is not null)
            {
                return containedName;
            }
        }
        return null;
    }

    private static SongInputEffectChain CreateFallbackChain(
        int channelIndex,
        string instrument,
        IReadOnlyList<InstalledEffectDescriptor> available)
    {
        var roles = channelIndex == 0
            ? new[]
            {
                new EffectRole("Amplificación o carácter", ["amp simulator", "guitar rig", "gtr amp", "amplifier", "distortion", "overdrive"]),
                new EffectRole("Control dinámico", ["dynamics", "compressor"]),
                new EffectRole("Ecualización", ["channel strip", "equalizer", " eq "]),
                new EffectRole("Ambiente", ["reverb", "delay"])
            }
            : new[]
            {
                new EffectRole("Control vocal", ["vocal", "voice", "deesser", "dynamics", "compressor"]),
                new EffectRole("Ecualización", ["channel strip", "equalizer", " eq "]),
                new EffectRole("Ambiente", ["reverb"]),
                new EffectRole("Profundidad", ["delay", "modulation"])
            };
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new List<SongEffectSlotRecommendation>();
        foreach (var role in roles)
        {
            var effect = available
                .Where(candidate => !selected.Contains(candidate.CatalogId))
                .Select(candidate => new
                {
                    Effect = candidate,
                    Score = FallbackScore(candidate, role.Terms, channelIndex)
                })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Effect.Reference.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(candidate => candidate.Effect)
                .FirstOrDefault();
            if (effect is null)
            {
                continue;
            }
            selected.Add(effect.CatalogId);
            slots.Add(new SongEffectSlotRecommendation(
                effect.Reference,
                effect.EffectType,
                role.Purpose,
                role.Purpose,
                role.Purpose is "Ambiente" or "Profundidad" ? 0.25d : 1d));
        }
        return new SongInputEffectChain(
            channelIndex,
            instrument,
            "Cadena conservadora completada con efectos instalados.",
            slots);
    }

    private static int FallbackScore(
        InstalledEffectDescriptor effect,
        IReadOnlyList<string> terms,
        int channelIndex)
    {
        var text = $" {NormalizePluginText($"{effect.EffectType} {effect.Reference.Name} {effect.Reference.SubCategories}")} ";
        var matches = terms.Count(term => text.Contains(term, StringComparison.Ordinal));
        if (matches == 0)
        {
            return 0;
        }
        var score = matches * 25;
        score += effect.Reference.Name.Contains("Mono", StringComparison.OrdinalIgnoreCase) ? 12 : 0;
        score -= effect.Reference.Name.Contains("Stereo", StringComparison.OrdinalIgnoreCase) ? 8 : 0;
        score -= ContainsAny(text, "surround", "quad", "ambi", "5 1", "7 1") ? 100 : 0;
        if (channelIndex == 0 && ContainsAny(text, "guitar rig", "gtr amp", "amp simulator"))
        {
            score += 80;
        }
        if (channelIndex == 0 && ContainsAny(text, "vocal", "voice", "deesser"))
        {
            score -= 80;
        }
        if (channelIndex == 1 && ContainsAny(text, "vocal", "voice", "deesser"))
        {
            score += 60;
        }
        if (channelIndex == 1 && ContainsAny(text, "guitar rig", "gtr amp", "amp simulator"))
        {
            score -= 80;
        }
        return score;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string name,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string NormalizePluginText(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();

    private static bool ContainsAny(string text, params string[] terms) =>
        terms.Any(term => text.Contains(term, StringComparison.Ordinal));

    private sealed record EffectRole(string Purpose, IReadOnlyList<string> Terms);

    private static string GetString(JsonElement element, string property, string fallback) =>
        TryGetPropertyIgnoreCase(element, property, out var value) &&
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
