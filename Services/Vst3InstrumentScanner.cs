using DrumPracticeStudio.Models;
using NAudio.Vst3;

namespace DrumPracticeStudio.Services;

public sealed record Vst3ScanResult(
    IReadOnlyList<Vst3InstrumentItem> Instruments,
    int FailedModules);

public sealed class Vst3InstrumentScanner
{
    public Task<Vst3ScanResult> ScanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Scan(cancellationToken), cancellationToken);

    private static Vst3ScanResult Scan(CancellationToken cancellationToken)
    {
        var instruments = new List<Vst3InstrumentItem>();
        var failedModules = 0;

        foreach (var moduleInfo in Vst3PluginScanner.EnumerateInstalled())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Vst3Module? module = null;
            try
            {
                module = Vst3Module.Load(moduleInfo.Path);
                foreach (var pluginClass in module.GetClasses().Where(candidate => candidate.IsInstrument))
                {
                    instruments.Add(new Vst3InstrumentItem(moduleInfo, pluginClass));
                }
            }
            catch
            {
                failedModules++;
            }
            finally
            {
                module?.Dispose();
            }
        }

        var ordered = instruments
            .OrderByDescending(instrument => instrument.IsPreferredDrumInstrument)
            .ThenBy(instrument => instrument.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new Vst3ScanResult(ordered, failedModules);
    }
}
