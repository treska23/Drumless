using NAudio.Vst3;

namespace DrumPracticeStudio.Models;

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

        var searchableText =
            $"{DisplayName} {Vendor} {EffectType} {PluginClass.SubCategories} {Module.Name}";
        return query.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(term => searchableText.Contains(term, StringComparison.CurrentCultureIgnoreCase));
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
}
