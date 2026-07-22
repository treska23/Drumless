using System.Text.Json;
using DrumPracticeStudio.Models;
using NAudio.Vst3;

namespace DrumPracticeStudio.Services;

internal sealed record Vst3ParameterProbeConfiguration(
    Vst3EffectReference Effect,
    int SampleRate,
    int MaximumBlockFrames,
    string OutputPath);

public sealed record Vst3ParameterDescriptor(
    uint Id,
    string Title,
    string ShortTitle,
    string Units,
    int StepCount,
    double DefaultNormalizedValue,
    string DefaultDisplayValue,
    double CurrentNormalizedValue,
    string CurrentDisplayValue);

internal static class Vst3ParameterProbeProtocol
{
    public const string Argument = "--vst3-effect-parameters";

    public static int Execute(string configurationPath)
    {
        Vst3Module? module = null;
        Vst3Plugin? plugin = null;
        try
        {
            var configuration = JsonSerializer.Deserialize<Vst3ParameterProbeConfiguration>(
                                    File.ReadAllText(configurationPath))
                                ?? throw new InvalidDataException("Configuración de parámetros vacía.");
            module = Vst3Module.Load(configuration.Effect.ModulePath);
            plugin = module.CreatePlugin(
                new Vst3ClassInfo(
                    configuration.Effect.ClassId,
                    configuration.Effect.Category,
                    configuration.Effect.Name,
                    configuration.Effect.Vendor,
                    configuration.Effect.Version,
                    configuration.Effect.SdkVersion,
                    configuration.Effect.SubCategories),
                configuration.SampleRate,
                configuration.MaximumBlockFrames);
            TryRecoverController(plugin, module);
            if (!string.IsNullOrWhiteSpace(configuration.Effect.PresetPath) &&
                File.Exists(configuration.Effect.PresetPath))
            {
                plugin.LoadPreset(configuration.Effect.PresetPath);
            }
            ApplyConfiguredParameters(plugin, configuration.Effect);
            var parameters = plugin.Parameters
                .Where(parameter =>
                    !parameter.IsHidden &&
                    !parameter.IsReadOnly &&
                    !parameter.IsBypass &&
                    !parameter.IsProgramChange &&
                    !string.IsNullOrWhiteSpace(parameter.Title))
                .Select(parameter => new Vst3ParameterDescriptor(
                    parameter.Id,
                    parameter.Title.Trim(),
                    string.IsNullOrWhiteSpace(parameter.ShortTitle)
                        ? parameter.Title.Trim()
                        : parameter.ShortTitle.Trim(),
                    parameter.Units?.Trim() ?? string.Empty,
                    parameter.StepCount,
                    Math.Clamp(parameter.DefaultNormalizedValue, 0d, 1d),
                    FormatValue(parameter, parameter.DefaultNormalizedValue),
                    ReadCurrentValue(parameter),
                    FormatCurrent(parameter)))
                .Take(256)
                .ToArray();
            File.WriteAllText(configuration.OutputPath, JsonSerializer.Serialize(parameters));
            return 0;
        }
        catch
        {
            return 1;
        }
        finally
        {
            plugin?.Dispose();
            module?.Dispose();
            TryDelete(configurationPath);
        }
    }

    private static void TryRecoverController(Vst3Plugin plugin, Vst3Module module)
    {
        if (plugin.Parameters.Count > 0)
        {
            return;
        }
        if (Vst3SameObjectControllerRecovery.TryRecover(plugin, out _))
        {
            return;
        }
        if (Vst3WaveShellControllerProbe.TryRecover(plugin, module, out _))
        {
            return;
        }
        _ = Vst3ControllerRecovery.TryRecoverForEditor(
            plugin,
            module,
            replaceExistingController: true);
    }

    private static void ApplyConfiguredParameters(
        Vst3Plugin plugin,
        Vst3EffectReference effect)
    {
        foreach (var setting in effect.EffectiveParameterSettings)
        {
            try
            {
                var parameter = plugin.Parameters.TryGetById(setting.Id, out var byId)
                    ? byId
                    : plugin.Parameters.FindByTitle(setting.Title);
                if (parameter is null || parameter.IsReadOnly || parameter.IsBypass ||
                    parameter.IsProgramChange)
                {
                    continue;
                }
                parameter.NormalizedValue = setting.NormalizedValue;
            }
            catch
            {
            }
        }
    }

    private static double ReadCurrentValue(Vst3Parameter parameter)
    {
        try
        {
            return Math.Clamp(parameter.NormalizedValue, 0d, 1d);
        }
        catch
        {
            return Math.Clamp(parameter.DefaultNormalizedValue, 0d, 1d);
        }
    }

    private static string FormatCurrent(Vst3Parameter parameter) =>
        FormatValue(parameter, ReadCurrentValue(parameter));

    private static string FormatValue(Vst3Parameter parameter, double normalized)
    {
        try
        {
            return parameter.FormatValue(normalized);
        }
        catch
        {
            return $"{normalized:P0}";
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
