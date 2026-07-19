using DrumPracticeStudio.Models;
using NAudio.Vst3;

namespace DrumPracticeStudio.Services;

public sealed record Vst3EffectScanResult(
    IReadOnlyList<Vst3EffectItem> Effects,
    int FailedModules);

public sealed class Vst3EffectScanner
{
    public Task<Vst3EffectScanResult> ScanAsync(
        CancellationToken cancellationToken = default) =>
        ScanAsync([], cancellationToken);

    public async Task<Vst3EffectScanResult> ScanAsync(
        IEnumerable<string> additionalFolders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(additionalFolders);
        var folders = additionalFolders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var modules = await Task.Run(
            () => Vst3PluginScanner.EnumerateInstalled()
                .Concat(folders.SelectMany(Vst3PluginScanner.EnumerateIn))
                .GroupBy(module => module.Path, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray(),
            cancellationToken);
        var effects = new List<Vst3EffectItem>();
        var failed = 0;
        foreach (var module in modules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var classes = await Vst3InstrumentScanner.ProbeModuleAsync(
                    module.Path,
                    cancellationToken);
                foreach (var candidate in classes.Where(candidate => !candidate.IsInstrument))
                {
                    effects.Add(new Vst3EffectItem(
                        module,
                        new Vst3ClassInfo(
                            candidate.ClassId,
                            candidate.Category,
                            candidate.Name,
                            candidate.Vendor,
                            candidate.Version,
                            candidate.SdkVersion,
                            candidate.SubCategories)));
                }
            }
            catch
            {
                failed++;
            }
        }

        return new Vst3EffectScanResult(
            effects
                .OrderBy(effect => effect.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(effect => effect.Vendor, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            failed);
    }
}
