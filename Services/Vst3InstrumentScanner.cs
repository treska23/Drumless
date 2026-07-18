using System.Diagnostics;
using System.Text.Json;
using DrumPracticeStudio.Models;
using NAudio.Vst3;

namespace DrumPracticeStudio.Services;

public sealed record Vst3ScanResult(
    IReadOnlyList<Vst3InstrumentItem> Instruments,
    int FailedModules);

public sealed class Vst3InstrumentScanner
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    public async Task<Vst3ScanResult> ScanAsync(CancellationToken cancellationToken = default)
    {
        var instruments = new List<Vst3InstrumentItem>();
        var failedModules = 0;
        var candidates = await Task.Run(
            () => Vst3PluginScanner.EnumerateInstalled()
                .Where(IsRequestedDrumInstrument)
                .ToArray(),
            cancellationToken);

        foreach (var moduleInfo in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var classes = await ProbeModuleAsync(moduleInfo.Path, cancellationToken);
                foreach (var probedClass in classes.Where(candidate => candidate.IsInstrument))
                {
                    var pluginClass = new Vst3ClassInfo(
                        probedClass.ClassId,
                        probedClass.Category,
                        probedClass.Name,
                        probedClass.Vendor,
                        probedClass.Version,
                        probedClass.SdkVersion,
                        probedClass.SubCategories);
                    instruments.Add(new Vst3InstrumentItem(moduleInfo, pluginClass));
                }
            }
            catch
            {
                failedModules++;
            }
        }

        var ordered = instruments
            .OrderByDescending(instrument => instrument.IsPreferredDrumInstrument)
            .ThenBy(instrument => instrument.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new Vst3ScanResult(ordered, failedModules);
    }

    private static bool IsRequestedDrumInstrument(Vst3ModuleInfo module) =>
        module.Name.Contains("Addictive Drums", StringComparison.OrdinalIgnoreCase) ||
        module.Name.Contains("Groove Agent", StringComparison.OrdinalIgnoreCase);

    internal static async Task<IReadOnlyList<Vst3ProbedClass>> ProbeModuleAsync(
        string modulePath,
        CancellationToken cancellationToken)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("No se pudo localizar el ejecutable de Drum Practice Studio.");
        }

        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"DrumPracticeStudio.Vst3Probe.{Guid.NewGuid():N}.json");
        try
        {
            var startInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add(Vst3ProbeProtocol.Argument);
            startInfo.ArgumentList.Add(modulePath);
            startInfo.ArgumentList.Add(outputPath);

            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("No se pudo iniciar el analizador VST3 aislado.");
            try
            {
                await process.WaitForExitAsync(cancellationToken).WaitAsync(ProbeTimeout, cancellationToken);
            }
            catch
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // El proceso puede haberse cerrado justo al expirar el tiempo.
                }

                throw;
            }

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException("El instrumento falló durante el análisis aislado.");
            }

            var json = await File.ReadAllTextAsync(outputPath, cancellationToken);
            return JsonSerializer.Deserialize<Vst3ProbedClass[]>(json) ?? [];
        }
        finally
        {
            try
            {
                File.Delete(outputPath);
            }
            catch
            {
                // La limpieza del informe temporal no debe bloquear la búsqueda.
            }
        }
    }
}
