using System.Text.Json;
using DrumPracticeStudio.Models;
using NAudio.Vst3;

namespace DrumPracticeStudio.Services;

internal sealed record Vst3EffectRuntimeConfiguration(
    Vst3EffectReference Effect,
    int SampleRate,
    int MaximumBlockFrames,
    string ReadyPath,
    string DiagnosticPath);

internal sealed record Vst3EffectRuntimeReady(
    bool Ready,
    uint LatencySamples,
    string Message);

internal static class Vst3EffectRuntimeProtocol
{
    public const string Argument = "--vst3-effect-runtime";

    public static int Execute(string configurationPath)
    {
        Vst3Module? module = null;
        Vst3Plugin? plugin = null;
        Vst3EffectRuntimeConfiguration? configuration = null;
        try
        {
            configuration = JsonSerializer.Deserialize<Vst3EffectRuntimeConfiguration>(
                                File.ReadAllText(configurationPath))
                            ?? throw new InvalidDataException("Configuración VST3 vacía.");
            TryDelete(configurationPath);
            module = Vst3Module.Load(configuration.Effect.ModulePath);
            var pluginClass = new Vst3ClassInfo(
                configuration.Effect.ClassId,
                configuration.Effect.Category,
                configuration.Effect.Name,
                configuration.Effect.Vendor,
                configuration.Effect.Version,
                configuration.Effect.SdkVersion,
                configuration.Effect.SubCategories);
            plugin = module.CreatePlugin(
                pluginClass,
                configuration.SampleRate,
                configuration.MaximumBlockFrames);
            if (plugin.IsInstrument)
            {
                throw new InvalidOperationException(
                    $"{configuration.Effect.Name} es un instrumento, no un efecto.");
            }
            if (plugin.InputChannelCount != 2 || plugin.OutputChannelCount != 2)
            {
                throw new NotSupportedException(
                    $"{configuration.Effect.Name} expone {plugin.InputChannelCount} entrada(s) y " +
                    $"{plugin.OutputChannelCount} salida(s); se necesita estéreo 2→2.");
            }
            if (!string.IsNullOrWhiteSpace(configuration.Effect.PresetPath))
            {
                plugin.LoadPreset(configuration.Effect.PresetPath);
            }

            WriteReady(configuration.ReadyPath, new Vst3EffectRuntimeReady(
                true,
                plugin.LatencySamples,
                "Efecto preparado"));
            RunAudioLoop(
                plugin,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                configuration.MaximumBlockFrames);
            return 0;
        }
        catch (Exception exception)
        {
            if (configuration is not null)
            {
                WriteDiagnostic(configuration.DiagnosticPath, exception);
                WriteReady(configuration.ReadyPath, new Vst3EffectRuntimeReady(
                    false,
                    0,
                    $"{exception.GetType().Name}: {exception.Message}"));
            }
            return 1;
        }
        finally
        {
            plugin?.Dispose();
            module?.Dispose();
            TryDelete(configurationPath);
        }
    }

    private static void RunAudioLoop(
        Vst3Plugin plugin,
        Stream input,
        Stream output,
        int maximumBlockFrames)
    {
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        var inputBuffer = new float[Math.Max(1, maximumBlockFrames) * 2];
        var outputBuffer = new float[Math.Max(1, maximumBlockFrames) * 2];
        while (true)
        {
            var frames = reader.ReadInt32();
            if (frames <= 0)
            {
                break;
            }

            writer.Write(frames);
            var remaining = frames;
            while (remaining > 0)
            {
                var chunkFrames = Math.Min(remaining, maximumBlockFrames);
                var samples = chunkFrames * 2;
                for (var index = 0; index < samples; index++)
                {
                    inputBuffer[index] = reader.ReadSingle();
                }
                plugin.Process(
                    inputBuffer.AsSpan(0, samples),
                    outputBuffer.AsSpan(0, samples),
                    chunkFrames);
                for (var index = 0; index < samples; index++)
                {
                    writer.Write(outputBuffer[index]);
                }
                remaining -= chunkFrames;
            }
            writer.Flush();
        }
    }

    private static void WriteReady(string path, Vst3EffectRuntimeReady ready)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(ready));
        }
        catch
        {
        }
    }

    private static void WriteDiagnostic(string path, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}");
        }
        catch
        {
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
