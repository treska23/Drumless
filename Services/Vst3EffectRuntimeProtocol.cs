using System.Text.Json;
using System.Windows;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Views;
using NAudio.Vst3;

namespace DrumPracticeStudio.Services;

internal sealed record Vst3EffectRuntimeConfiguration(
    Vst3EffectReference Effect,
    int SampleRate,
    int MaximumBlockFrames,
    string ReadyPath,
    string DiagnosticPath,
    string? StatePath = null,
    string? PipeName = null);

internal sealed record Vst3EffectRuntimeReady(
    bool Ready,
    uint LatencySamples,
    string Message,
    bool HasEditor = false);

internal static class Vst3EffectRuntimeProtocol
{
    public const string Argument = "--vst3-effect-runtime";
    internal const int OpenEditorCommand = -1;
    internal const int CloseEditorCommand = -2;

    public static void Start(string configurationPath) =>
        _ = RunAsync(configurationPath);

    // Se conserva como punto de entrada síncrono para pruebas de arranque y diagnóstico.
    public static int Execute(string configurationPath)
    {
        Vst3EffectRuntimeConfiguration? configuration = null;
        EffectRuntime? runtime = null;
        try
        {
            configuration = ReadConfiguration(configurationPath);
            runtime = EffectRuntime.Load(configuration, createEditorView: false);
            WriteReady(configuration.ReadyPath, new Vst3EffectRuntimeReady(
                true,
                runtime.LatencySamples,
                "Efecto preparado"));
            return 0;
        }
        catch (Exception exception)
        {
            ReportFailure(configuration, exception);
            return 1;
        }
        finally
        {
            runtime?.Dispose();
            TryDelete(configurationPath);
        }
    }

    private static async Task RunAsync(string configurationPath)
    {
        Vst3EffectRuntimeConfiguration? configuration = null;
        EffectRuntime? runtime = null;
        try
        {
            configuration = ReadConfiguration(configurationPath);
            var activeConfiguration = configuration;
            runtime = await Application.Current.Dispatcher.InvokeAsync(
                () => EffectRuntime.Load(activeConfiguration, createEditorView: true));
            WriteReady(configuration.ReadyPath, new Vst3EffectRuntimeReady(
                true,
                runtime.LatencySamples,
                "Efecto preparado",
                runtime.HasEditor));

            var activeRuntime = runtime;
            if (string.IsNullOrWhiteSpace(activeConfiguration.PipeName))
            {
                throw new InvalidDataException("El efecto VST3 no recibió una tubería de audio dedicada.");
            }
            using var pipe = new NamedPipeClientStream(
                ".",
                activeConfiguration.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(30_000);
            await Task.Run(() => RunMessageLoop(
                activeRuntime,
                pipe,
                pipe,
                activeConfiguration.MaximumBlockFrames,
                activeConfiguration.DiagnosticPath));
        }
        catch (Exception exception)
        {
            ReportFailure(configuration, exception);
        }
        finally
        {
            if (runtime is not null)
            {
                try
                {
                    await Application.Current.Dispatcher.InvokeAsync(runtime.Dispose);
                }
                catch (Exception exception)
                {
                    WriteDiagnostic(configuration?.DiagnosticPath, exception);
                }
            }
            TryDelete(configurationPath);
            Application.Current.Dispatcher.Invoke(Application.Current.Shutdown);
        }
    }

    private static Vst3EffectRuntimeConfiguration ReadConfiguration(string path)
    {
        var configuration = JsonSerializer.Deserialize<Vst3EffectRuntimeConfiguration>(
                                File.ReadAllText(path))
                            ?? throw new InvalidDataException("Configuración VST3 vacía.");
        TryDelete(path);
        return configuration;
    }

    private static void RunMessageLoop(
        EffectRuntime runtime,
        Stream input,
        Stream output,
        int maximumBlockFrames,
        string? diagnosticPath)
    {
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        var hostInput = new float[Math.Max(1, maximumBlockFrames) * 2];
        var pluginInput = new float[
            Math.Max(1, maximumBlockFrames) * Math.Max(1, runtime.Plugin.InputChannelCount)];
        var pluginOutput = new float[
            Math.Max(1, maximumBlockFrames) * runtime.Plugin.OutputChannelCount];
        var isFirstAudioBlock = true;
        while (true)
        {
            int message;
            try
            {
                message = reader.ReadInt32();
            }
            catch (Exception exception) when (exception is
                EndOfStreamException or
                IOException)
            {
                // The owning application closes the pipe to shut down or replace the slot.
                break;
            }
            if (message == 0)
            {
                break;
            }
            if (message < 0)
            {
                ExecuteControlCommand(runtime, message, writer);
                continue;
            }

            var frames = message;
            var remaining = frames;
            var wroteResponseHeader = false;
            while (remaining > 0)
            {
                var chunkFrames = Math.Min(remaining, maximumBlockFrames);
                var samples = chunkFrames * 2;
                reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(
                    hostInput.AsSpan(0, samples)));
                ConvertHostInput(
                    hostInput.AsSpan(0, samples),
                    pluginInput,
                    chunkFrames,
                    runtime.Plugin.InputChannelCount);
                if (isFirstAudioBlock)
                {
                    WriteDiagnostic(
                        diagnosticPath,
                        $"Procesando primer bloque: {chunkFrames} frames, " +
                        $"{runtime.Plugin.InputChannelCount}→{runtime.Plugin.OutputChannelCount} canales.");
                }
                runtime.Plugin.Process(
                    pluginInput.AsSpan(0, chunkFrames * runtime.Plugin.InputChannelCount),
                    pluginOutput.AsSpan(0, chunkFrames * runtime.Plugin.OutputChannelCount),
                    chunkFrames);
                if (isFirstAudioBlock)
                {
                    WriteDiagnostic(diagnosticPath, "Primer bloque procesado correctamente.");
                    isFirstAudioBlock = false;
                }
                if (!wroteResponseHeader)
                {
                    writer.Write(frames);
                    wroteResponseHeader = true;
                }
                WriteHostOutput(
                    writer,
                    pluginOutput,
                    chunkFrames,
                    runtime.Plugin.OutputChannelCount);
                remaining -= chunkFrames;
            }
            writer.Flush();
        }
    }

    internal static void ConvertHostInput(
        ReadOnlySpan<float> stereoInput,
        Span<float> pluginInput,
        int frames,
        int pluginChannels)
    {
        if (pluginChannels == 1)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                pluginInput[frame] =
                    (stereoInput[frame * 2] + stereoInput[(frame * 2) + 1]) * 0.5f;
            }
            return;
        }
        stereoInput[..(frames * 2)].CopyTo(pluginInput);
    }

    internal static void ConvertPluginOutput(
        ReadOnlySpan<float> pluginOutput,
        Span<float> stereoOutput,
        int frames,
        int pluginChannels)
    {
        if (pluginChannels == 1)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var sample = pluginOutput[frame];
                stereoOutput[frame * 2] = sample;
                stereoOutput[(frame * 2) + 1] = sample;
            }
            return;
        }
        pluginOutput[..(frames * 2)].CopyTo(stereoOutput);
    }

    private static void WriteHostOutput(
        BinaryWriter writer,
        ReadOnlySpan<float> pluginOutput,
        int frames,
        int pluginChannels)
    {
        if (pluginChannels == 1)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                writer.Write(pluginOutput[frame]);
                writer.Write(pluginOutput[frame]);
            }
            return;
        }
        // Mono needs duplication above; stereo can be transferred as one contiguous block.
        writer.Write(MemoryMarshal.AsBytes(pluginOutput[..(frames * 2)]));
    }

    private static void ExecuteControlCommand(
        EffectRuntime runtime,
        int command,
        BinaryWriter writer)
    {
        try
        {
            var message = Application.Current.Dispatcher.Invoke(() => command switch
            {
                OpenEditorCommand => runtime.OpenEditor(),
                CloseEditorCommand => runtime.CloseEditor(),
                _ => throw new InvalidDataException($"Orden VST3 desconocida: {command}.")
            });
            writer.Write(true);
            writer.Write(message);
        }
        catch (Exception exception)
        {
            writer.Write(false);
            writer.Write($"{exception.GetType().Name}: {exception.Message}");
        }
        writer.Flush();
    }

    private static void ReportFailure(
        Vst3EffectRuntimeConfiguration? configuration,
        Exception exception)
    {
        if (configuration is null)
        {
            return;
        }
        WriteDiagnostic(configuration.DiagnosticPath, exception);
        WriteReady(configuration.ReadyPath, new Vst3EffectRuntimeReady(
            false,
            0,
            $"{exception.GetType().Name}: {exception.Message}"));
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

    private static void WriteDiagnostic(string? path, Exception exception)
    {
        WriteDiagnostic(path, exception.ToString());
    }

    private static void WriteDiagnostic(string? path, string message)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
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

    private sealed class EffectRuntime : IDisposable
    {
        private readonly Vst3Module _module;
        private readonly Vst3PluginView? _view;
        private readonly string? _statePath;
        private Vst3EditorWindow? _editor;

        private EffectRuntime(
            Vst3Module module,
            Vst3Plugin plugin,
            Vst3PluginView? view,
            string displayName,
            string? statePath)
        {
            _module = module;
            Plugin = plugin;
            _view = view;
            DisplayName = displayName;
            _statePath = statePath;
        }

        public Vst3Plugin Plugin { get; }
        public string DisplayName { get; }
        public uint LatencySamples => Plugin.LatencySamples;
        public bool HasEditor => _view is not null;

        public static EffectRuntime Load(
            Vst3EffectRuntimeConfiguration configuration,
            bool createEditorView)
        {
            Vst3Module? module = null;
            Vst3Plugin? plugin = null;
            Vst3PluginView? view = null;
            try
            {
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
                if (plugin.InputChannelCount is not (1 or 2) ||
                    plugin.OutputChannelCount is not (1 or 2))
                {
                    throw new NotSupportedException(
                        $"{configuration.Effect.Name} expone {plugin.InputChannelCount} entrada(s) y " +
                        $"{plugin.OutputChannelCount} salida(s); sólo se admiten buses mono o estéreo.");
                }

                var loadPath = File.Exists(configuration.StatePath)
                    ? configuration.StatePath
                    : configuration.Effect.PresetPath;
                if (!string.IsNullOrWhiteSpace(loadPath))
                {
                    plugin.LoadPreset(loadPath);
                }
                if (createEditorView)
                {
                    try
                    {
                        view = plugin.CreateView();
                    }
                    catch (Exception exception)
                    {
                        WriteDiagnostic(configuration.DiagnosticPath, exception);
                    }
                }

                return new EffectRuntime(
                    module,
                    plugin,
                    view,
                    configuration.Effect.Name,
                    configuration.StatePath);
            }
            catch
            {
                view?.Dispose();
                plugin?.Dispose();
                module?.Dispose();
                throw;
            }
        }

        public string OpenEditor()
        {
            if (_view is null)
            {
                throw new NotSupportedException(
                    $"{DisplayName} no proporciona una interfaz VST3 compatible.");
            }
            if (_editor is not null)
            {
                _editor.Activate();
                return $"La interfaz de {DisplayName} ya estaba abierta.";
            }

            _editor = new Vst3EditorWindow(DisplayName, _view)
            {
                ShowInTaskbar = true
            };
            _editor.ClosedByUser += (_, _) =>
            {
                _editor = null;
                SaveState();
            };
            _editor.Show();
            _editor.Activate();
            return $"Interfaz de {DisplayName} abierta.";
        }

        public string CloseEditor()
        {
            var editor = _editor;
            _editor = null;
            if (editor is null)
            {
                SaveState();
            }
            else
            {
                editor.Close();
            }
            return $"Interfaz de {DisplayName} cerrada.";
        }

        public void Dispose()
        {
            try
            {
                CloseEditor();
            }
            catch
            {
            }
            _view?.Dispose();
            Plugin.Dispose();
            _module.Dispose();
        }

        private void SaveState()
        {
            if (string.IsNullOrWhiteSpace(_statePath))
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                Plugin.SavePreset(_statePath);
            }
            catch
            {
                // Algunos plugins no admiten guardar estado; el cierre debe continuar.
            }
        }
    }
}
