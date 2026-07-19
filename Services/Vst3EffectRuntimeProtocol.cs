using System.Text.Json;
using System.Windows;
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
    string? StatePath = null);

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
            await Task.Run(() => RunMessageLoop(
                activeRuntime,
                Console.OpenStandardInput(),
                Console.OpenStandardOutput(),
                activeConfiguration.MaximumBlockFrames));
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
        int maximumBlockFrames)
    {
        using var reader = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        var inputBuffer = new float[Math.Max(1, maximumBlockFrames) * 2];
        var outputBuffer = new float[Math.Max(1, maximumBlockFrames) * 2];
        while (true)
        {
            var message = reader.ReadInt32();
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
                runtime.Plugin.Process(
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
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
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
                if (plugin.InputChannelCount != 2 || plugin.OutputChannelCount != 2)
                {
                    throw new NotSupportedException(
                        $"{configuration.Effect.Name} expone {plugin.InputChannelCount} entrada(s) y " +
                        $"{plugin.OutputChannelCount} salida(s); se necesita estéreo 2→2.");
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
