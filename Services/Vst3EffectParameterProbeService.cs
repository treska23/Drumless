using System.Diagnostics;
using System.Text.Json;
using DrumPracticeStudio.Audio;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed class Vst3EffectParameterProbeService
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(20);

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<Vst3ParameterDescriptor>>> ProbeAsync(
        IEnumerable<Vst3EffectReference> effects,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(effects);
        var result = new Dictionary<string, IReadOnlyList<Vst3ParameterDescriptor>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var effect in effects
                     .GroupBy(
                         item => Vst3EffectItem.GetCatalogId(item.ModulePath, item.ClassId),
                         StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.First()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Vst3EffectItem.GetCatalogId(effect.ModulePath, effect.ClassId);
            try
            {
                result[key] = await ProbeOneAsync(effect, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                result[key] = [];
            }
        }
        return result;
    }

    private static async Task<IReadOnlyList<Vst3ParameterDescriptor>> ProbeOneAsync(
        Vst3EffectReference effect,
        CancellationToken cancellationToken)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("No se pudo localizar el ejecutable de Drum Practice Studio.");
        }
        var token = Guid.NewGuid().ToString("N");
        var configurationPath = Path.Combine(
            Path.GetTempPath(),
            $"DrumPracticeStudio.Vst3Parameters.{token}.config.json");
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"DrumPracticeStudio.Vst3Parameters.{token}.result.json");
        try
        {
            await File.WriteAllTextAsync(
                configurationPath,
                JsonSerializer.Serialize(new Vst3ParameterProbeConfiguration(
                    effect,
                    AudioEngine.SampleRate,
                    AudioLatencySettings.VstMaxBlockSize,
                    outputPath)),
                cancellationToken);
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            startInfo.ArgumentList.Add(Vst3ParameterProbeProtocol.Argument);
            startInfo.ArgumentList.Add(configurationPath);
            using var process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("No se pudo iniciar el lector VST3 aislado.");
            try
            {
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(ProbeTimeout, cancellationToken);
            }
            catch
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }
                throw;
            }
            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                throw new InvalidOperationException($"No se pudieron leer los parámetros de {effect.Name}.");
            }
            return JsonSerializer.Deserialize<Vst3ParameterDescriptor[]>(
                       await File.ReadAllTextAsync(outputPath, cancellationToken))
                   ?? [];
        }
        finally
        {
            TryDelete(configurationPath);
            TryDelete(outputPath);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }
}
