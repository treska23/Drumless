using NAudio.Vst3;
using System.Globalization;
using System.Text;

namespace DrumPracticeStudio.Models;

public enum Vst3EffectGroupingMode
{
    EffectType,
    Vendor,
    VendorThenEffectType,
    None
}

public sealed record Vst3EffectGroupingOption(
    Vst3EffectGroupingMode Mode,
    string Label);

public sealed class Vst3EffectItem(
    Vst3ModuleInfo module,
    Vst3ClassInfo pluginClass)
{
    private static readonly HashSet<string> GenericSubCategories =
    [
        "Fx",
        "Effect",
        "Mono",
        "Stereo",
        "Surround"
    ];

    public Vst3ModuleInfo Module { get; } = module;
    public Vst3ClassInfo PluginClass { get; } = pluginClass;

    public string DisplayName => string.IsNullOrWhiteSpace(PluginClass.Name)
        ? Module.Name
        : PluginClass.Name;
    public string Vendor => string.IsNullOrWhiteSpace(PluginClass.Vendor)
        ? "Fabricante desconocido"
        : PluginClass.Vendor;
    public string DisplayLabel => $"{DisplayName} · {Vendor}";
    public string CatalogId => GetCatalogId(Module.Path, PluginClass.ClassId);
    public string EffectType =>
        PluginClass.SubCategoryList
            .LastOrDefault(category => !GenericSubCategories.Contains(category))
        ?? "Otros efectos";

    public bool MatchesSearch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var normalizedQuery = NormalizeSearchText(query);
        if (normalizedQuery.Length == 0)
        {
            return true;
        }

        var searchableFields = new[]
            {
                DisplayName,
                Vendor,
                EffectType,
                PluginClass.SubCategories,
                Module.Name
            }
            .Select(NormalizeSearchText)
            .Where(field => field.Length > 0)
            .ToArray();
        var searchableText = string.Join(' ', searchableFields);
        var searchableTokens = searchableText.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var compactQuery = RemoveSpaces(normalizedQuery);

        // Al comparar también la forma compacta, "GuitarRig", "guitar rig" y hasta
        // "g u i t a r r i g" son equivalentes.
        if (RemoveSpaces(searchableText).Contains(compactQuery, StringComparison.Ordinal))
        {
            return true;
        }

        // Una errata en una búsqueda compuesta también debe poder encontrar el nombre completo.
        // Se comparan los campos por separado para no mezclar accidentalmente marca, tipo y nombre.
        if (searchableFields.Any(field =>
                IsFuzzyMatch(compactQuery, RemoveSpaces(field))))
        {
            return true;
        }

        return normalizedQuery
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => searchableTokens.Any(token =>
                token.Contains(term, StringComparison.Ordinal) ||
                IsFuzzyMatch(term, token)));
    }

    public Vst3EffectReference ToReference(string? presetPath = null) => new(
        Module.Path,
        Module.Name,
        PluginClass.ClassId,
        PluginClass.Category,
        PluginClass.Name,
        PluginClass.Vendor,
        PluginClass.Version,
        PluginClass.SdkVersion,
        PluginClass.SubCategories,
        presetPath);

    public static Vst3EffectItem FromReference(Vst3EffectReference reference) => new(
        new Vst3ModuleInfo(reference.ModulePath, reference.ModuleName),
        new Vst3ClassInfo(
            reference.ClassId,
            reference.Category,
            reference.Name,
            reference.Vendor,
            reference.Version,
            reference.SdkVersion,
            reference.SubCategories));

    public static string GetCatalogId(string modulePath, string classId) =>
        $"{Path.GetFullPath(modulePath)}|{classId.Trim()}";

    private static string NormalizeSearchText(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var previousWasSeparator = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                previousWasSeparator = false;
            }
            else if (!previousWasSeparator)
            {
                builder.Append(' ');
                previousWasSeparator = true;
            }
        }
        return builder.ToString().Trim();
    }

    private static string RemoveSpaces(string value) =>
        value.Replace(" ", string.Empty, StringComparison.Ordinal);

    private static bool IsFuzzyMatch(string query, string candidate)
    {
        // Con una o dos letras, una coincidencia aproximada produciría demasiados falsos
        // positivos. A partir de cuatro caracteres se toleran más errores de forma gradual.
        var maximumDistance = query.Length switch
        {
            <= 3 => 0,
            <= 5 => 1,
            <= 8 => 2,
            _ => 3
        };
        if (maximumDistance == 0 ||
            Math.Abs(query.Length - candidate.Length) > maximumDistance)
        {
            return false;
        }

        return GetDamerauLevenshteinDistance(
            query,
            candidate,
            maximumDistance) <= maximumDistance;
    }

    private static int GetDamerauLevenshteinDistance(
        string left,
        string right,
        int maximumDistance)
    {
        var previousPrevious = new int[right.Length + 1];
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            var smallestValue = current[0];
            for (var column = 1; column <= right.Length; column++)
            {
                var substitutionCost = left[row - 1] == right[column - 1] ? 0 : 1;
                var value = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + substitutionCost);

                if (row > 1 &&
                    column > 1 &&
                    left[row - 1] == right[column - 2] &&
                    left[row - 2] == right[column - 1])
                {
                    value = Math.Min(value, previousPrevious[column - 2] + 1);
                }

                current[column] = value;
                smallestValue = Math.Min(smallestValue, value);
            }

            if (smallestValue > maximumDistance)
            {
                return maximumDistance + 1;
            }

            (previousPrevious, previous, current) = (previous, current, previousPrevious);
        }

        return previous[right.Length];
    }
}
