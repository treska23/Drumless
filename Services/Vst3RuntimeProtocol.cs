using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using DrumPracticeStudio.Audio;
using DrumPracticeStudio.Views;
using NAudio.Vst3;
using NAudio.Wave;

namespace DrumPracticeStudio.Services;

internal sealed record Vst3RuntimeConfiguration(
    string ModulePath,
    string ModuleName,
    string ClassId,
    string Category,
    string Name,
    string Vendor,
    string Version,
    string SdkVersion,
    string SubCategories,
    int SampleRate,
    string? OutputDeviceId);

internal sealed record Vst3RuntimeResponse(
    bool Ready,
    bool HasEditor,
    string Message,
    string[]? Programs = null,
    int CurrentProgram = -1);

internal sealed record Vst3RuntimeCommand(
    string Type,
    int Value1 = 0,
    int Value2 = 0,
    int Value3 = 0,
    string? Text = null);

internal static class Vst3RuntimeProtocol
{
    public const string Argument = "--vst3-runtime";

    public static void Start(string configurationPath, string pipeName) =>
        _ = RunAsync(configurationPath, pipeName);

    private static async Task RunAsync(string configurationPath, string pipeName)
    {
        using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        StreamReader? reader = null;
        StreamWriter? writer = null;
        Vst3Runtime? runtime = null;
        try
        {
            await pipe.ConnectAsync(15_000);
            reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1_024,
                leaveOpen: true);
            writer = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1_024,
                leaveOpen: true) { AutoFlush = true };

            var json = await File.ReadAllTextAsync(configurationPath);
            var configuration = JsonSerializer.Deserialize<Vst3RuntimeConfiguration>(json)
                                ?? throw new InvalidOperationException("La configuración VST3 está vacía.");
            TryDelete(configurationPath);

            runtime = await Application.Current.Dispatcher.InvokeAsync(
                () => Vst3Runtime.Load(configuration));
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new Vst3RuntimeResponse(
                    true,
                    runtime.HasEditor,
                    "Instrumento preparado",
                    runtime.Programs.ToArray(),
                    runtime.CurrentProgram)));

            while (await reader.ReadLineAsync() is { } line)
            {
                var command = JsonSerializer.Deserialize<Vst3RuntimeCommand>(line);
                if (command is null)
                {
                    continue;
                }

                if (string.Equals(command.Type, "Stop", StringComparison.Ordinal))
                {
                    break;
                }

                var activeRuntime = runtime
                                    ?? throw new InvalidOperationException("El motor VST3 no está preparado.");
                await Application.Current.Dispatcher.InvokeAsync(() =>
                    ExecuteCommand(activeRuntime, command));
            }
        }
        catch (Exception exception)
        {
            if (writer is not null)
            {
                try
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        new Vst3RuntimeResponse(false, false, exception.Message)));
                }
                catch
                {
                    // El proceso principal puede haber cerrado ya la tubería.
                }
            }
        }
        finally
        {
            TryDelete(configurationPath);
            if (runtime is not null)
            {
                await Application.Current.Dispatcher.InvokeAsync(runtime.Dispose);
            }
            reader?.Dispose();
            writer?.Dispose();
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
        }
    }

    private static void ExecuteCommand(Vst3Runtime runtime, Vst3RuntimeCommand command)
    {
        switch (command.Type)
        {
            case "NoteOn":
                runtime.Plugin.SendNoteOn(command.Value1, command.Value2 / 127f, command.Value3);
                break;
            case "NoteOff":
                runtime.Plugin.SendNoteOff(command.Value1, command.Value2 / 127f, command.Value3);
                break;
            case "ControlChange":
                runtime.Plugin.SendControlChange(command.Value1, command.Value2 / 127d);
                break;
            case "Panic":
                runtime.Plugin.AllNotesOff();
                break;
            case "OpenEditor":
                runtime.OpenEditor();
                break;
            case "CloseEditor":
                runtime.CloseEditor();
                break;
            case "SetOutput":
                runtime.SetOutput(command.Text);
                break;
            case "ProgramChange":
                runtime.SelectProgram(command.Value1);
                break;
            case "LoadPreset":
                runtime.LoadPreset(command.Text);
                break;
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
            // El archivo temporal se volverá a limpiar en el siguiente arranque.
        }
    }

    private sealed class Vst3Runtime : IDisposable
    {
        private readonly Vst3Module _module;
        private readonly Vst3PluginView? _view;
        private readonly ISampleProvider _provider;
        private AudioOutputSession _output;
        private Vst3EditorWindow? _editor;

        private Vst3Runtime(
            Vst3Module module,
            Vst3Plugin plugin,
            Vst3PluginView? view,
            ISampleProvider provider,
            AudioOutputSession output,
            string displayName)
        {
            _module = module;
            Plugin = plugin;
            _view = view;
            _provider = provider;
            _output = output;
            DisplayName = displayName;
        }

        public Vst3Plugin Plugin { get; }
        public string DisplayName { get; }
        public bool HasEditor => _view is not null;
        public IReadOnlyList<string> Programs =>
            Plugin.ActiveProgramList?.Programs ?? Array.Empty<string>();
        public int CurrentProgram => Plugin.CurrentProgram;

        public static Vst3Runtime Load(Vst3RuntimeConfiguration configuration)
        {
            Vst3Module? module = null;
            Vst3Plugin? plugin = null;
            Vst3PluginView? view = null;
            AudioOutputSession? output = null;
            try
            {
                module = Vst3Module.Load(configuration.ModulePath);
                var pluginClass = new Vst3ClassInfo(
                    configuration.ClassId,
                    configuration.Category,
                    configuration.Name,
                    configuration.Vendor,
                    configuration.Version,
                    configuration.SdkVersion,
                    configuration.SubCategories);
                plugin = module.CreatePlugin(pluginClass, configuration.SampleRate, 2_048);
                if (!plugin.IsInstrument)
                {
                    throw new InvalidOperationException(
                        $"{configuration.Name} no se identificó como instrumento VST3.");
                }

                var provider = new Vst3InstrumentSampleProvider(plugin);
                if (provider.WaveFormat.Channels != 2)
                {
                    throw new NotSupportedException(
                        $"{configuration.Name} ha abierto {provider.WaveFormat.Channels} canales; " +
                        "esta versión necesita una salida principal estéreo.");
                }

                view = plugin.CreateView();
                output = AudioOutputSession.Open(provider, configuration.OutputDeviceId);
                output.Play();
                return new Vst3Runtime(module, plugin, view, provider, output, configuration.Name);
            }
            catch
            {
                output?.Dispose();
                view?.Dispose();
                plugin?.Dispose();
                module?.Dispose();
                throw;
            }
        }

        public void OpenEditor()
        {
            if (_view is null)
            {
                return;
            }

            if (_editor is not null)
            {
                _editor.Activate();
                return;
            }

            _editor = new Vst3EditorWindow(DisplayName, _view)
            {
                ShowInTaskbar = true
            };
            _editor.ClosedByUser += (_, _) => _editor = null;
            _editor.Show();
            _editor.Activate();
        }

        public void CloseEditor()
        {
            var editor = _editor;
            _editor = null;
            try
            {
                editor?.Close();
            }
            catch
            {
                // La ventana puede estar cerrándose ya.
            }
        }

        public void SetOutput(string? deviceId)
        {
            var replacement = AudioOutputSession.Open(_provider, deviceId);
            var previous = _output;
            previous.Stop();
            try
            {
                replacement.Play();
            }
            catch
            {
                replacement.Dispose();
                previous.Play();
                throw;
            }

            _output = replacement;
            previous.Dispose();
        }

        public void SelectProgram(int programIndex)
        {
            if (!Plugin.SupportsProgramChange ||
                programIndex < 0 ||
                programIndex >= Programs.Count)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(programIndex),
                    "El instrumento no expone ese programa mediante VST3.");
            }

            Plugin.SendProgramChange(programIndex);
        }

        public void LoadPreset(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                throw new FileNotFoundException("No se encontró el preset VST3.", path);
            }

            Plugin.LoadPreset(path);
        }

        public void Dispose()
        {
            CloseEditor();
            try
            {
                Plugin.AllNotesOff();
            }
            catch
            {
                // El plugin puede estar cerrándose después de un fallo nativo.
            }
            _output.Dispose();
            _view?.Dispose();
            Plugin.Dispose();
            _module.Dispose();
        }

    }
}
