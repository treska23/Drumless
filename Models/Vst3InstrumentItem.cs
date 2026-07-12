using NAudio.Vst3;

namespace DrumPracticeStudio.Models;

public sealed class Vst3InstrumentItem(
    Vst3ModuleInfo module,
    Vst3ClassInfo pluginClass)
{
    public Vst3ModuleInfo Module { get; } = module;
    public Vst3ClassInfo PluginClass { get; } = pluginClass;

    public string DisplayName => string.IsNullOrWhiteSpace(PluginClass.Name)
        ? Module.Name
        : PluginClass.Name;

    public string Vendor => string.IsNullOrWhiteSpace(PluginClass.Vendor)
        ? "Fabricante desconocido"
        : PluginClass.Vendor;

    public string DisplayLabel => $"{DisplayName} · {Vendor}";

    public bool IsPreferredDrumInstrument =>
        DisplayName.Contains("Addictive Drums", StringComparison.OrdinalIgnoreCase) ||
        DisplayName.Contains("Groove Agent", StringComparison.OrdinalIgnoreCase);
}
