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
    string? OutputDeviceId,
    string DiagnosticPath);

internal sealed record Vst3RuntimeResponse(
    bool Ready,
    bool HasEditor,
    string Message,
    string[]? Programs = null,
    int CurrentProgram = -1,
    string? Detail = null,
    string? AudioStatus = null);

internal sealed record Vst3RuntimeCommand(
    string Type,
    int Value1 = 0,
    int Value2 = 0,
    int Value3 = 0,
    string? Text = null);

internal sealed record Vst3RuntimeNotification(string Type);

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
            Vst3RuntimeDiagnostics.Initialize(configuration.DiagnosticPath);
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Vst3RuntimeDiagnostics.Error(
                    "Excepción no controlada en el proceso VST3",
                    args.ExceptionObject as Exception);
            TryDelete(configurationPath);

            Vst3RuntimeDiagnostics.Info($"Cargando {configuration.Name} · {configuration.ModulePath}");
            runtime = await Application.Current.Dispatcher.InvokeAsync(
                () => Vst3Runtime.Load(configuration));
            runtime.EditorClosed += (_, _) =>
                TrySendNotification(writer!, new Vst3RuntimeNotification("EditorClosed"));
            Vst3RuntimeDiagnostics.Info("Instrumento preparado y salida de audio iniciada");
            Vst3RuntimeDiagnostics.Info(runtime.AudioStatus);
            await writer.WriteLineAsync(JsonSerializer.Serialize(
                new Vst3RuntimeResponse(
                    true,
                    runtime.HasEditor,
                    "Instrumento preparado",
                    runtime.Programs.ToArray(),
                    runtime.CurrentProgram,
                    AudioStatus: runtime.AudioStatus)));

            await RunCommandLoopAsync(reader, writer, runtime).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Vst3RuntimeDiagnostics.Error("El motor VST3 se detuvo", exception);
            if (writer is not null)
            {
                try
                {
                    await writer.WriteLineAsync(JsonSerializer.Serialize(
                        new Vst3RuntimeResponse(
                            false,
                            false,
                            $"{exception.GetType().Name}: {exception.Message}",
                            Detail: exception.ToString())));
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

    private static Task RunCommandLoopAsync(
        StreamReader reader,
        StreamWriter writer,
        Vst3Runtime runtime)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                while (reader.ReadLine() is { } line)
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

                    if (RequiresUiThread(command))
                    {
                        Application.Current.Dispatcher.Invoke(() => ExecuteCommand(runtime, command));
                    }
                    else
                    {
                        ExecuteCommand(runtime, command);
                    }

                    if (command.Type is "StartRecording" or "StopRecording")
                    {
                        TrySendNotification(
                            writer,
                            new Vst3RuntimeNotification(command.Type + "Completed"));
                    }

                    if (!IsRealtimeMidi(command))
                    {
                        Vst3RuntimeDiagnostics.Info(DescribeCommand(command));
                    }
                }
                completion.SetResult(true);
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "Drumless VST3 MIDI",
            Priority = ThreadPriority.AboveNormal
        };
        thread.Start();
        return completion.Task;
    }

    internal static bool IsRealtimeMidi(Vst3RuntimeCommand command) =>
        command.Type is "NoteOn" or "NoteOff" or "ControlChange" or "Panic";

    internal static bool RequiresUiThread(Vst3RuntimeCommand command) =>
        command.Type is "OpenEditor" or "CloseEditor" or "LoadPreset" or "SavePreset" or "SetOutput";

    private static void ExecuteCommand(Vst3Runtime runtime, Vst3RuntimeCommand command)
    {
        switch (command.Type)
        {
            case "NoteOn":
                runtime.Plugin.EnqueueNoteOn(
                    command.Value1,
                    command.Value2 / 127f,
                    ClampVstChannel(command.Value3));
                break;
            case "NoteOff":
                runtime.Plugin.EnqueueNoteOff(
                    command.Value1,
                    command.Value2 / 127f,
                    ClampVstChannel(command.Value3));
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
            case "SavePreset":
                runtime.SavePreset(command.Text);
                break;
            case "StartRecording":
                runtime.StartRecording(command.Text);
                break;
            case "StopRecording":
                runtime.StopRecording();
                break;
        }
    }

    private static string DescribeCommand(Vst3RuntimeCommand command) =>
        command.Type switch
        {
            "NoteOn" or "NoteOff" =>
                $"Orden recibida: {command.Type} · nota {command.Value1} · " +
                $"velocidad {command.Value2} · canal VST3 {command.Value3}",
            "ControlChange" =>
                $"Orden recibida: ControlChange · CC {command.Value1} · valor {command.Value2}",
            _ => $"Orden recibida: {command.Type}"
        };

    private static int ClampVstChannel(int vstChannel) =>
        Math.Clamp(vstChannel, 0, 15);

    private static void TrySendNotification(
        StreamWriter writer,
        Vst3RuntimeNotification notification)
    {
        try
        {
            lock (writer)
            {
                writer.WriteLine(JsonSerializer.Serialize(notification));
            }
        }
        catch
        {
            // El proceso principal puede estar cerrando la tubería.
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
        private readonly OutputRecordingSink _recording;
        private AudioOutputSession _output;
        private Vst3EditorWindow? _editor;

        private Vst3Runtime(
            Vst3Module module,
            Vst3Plugin plugin,
            Vst3PluginView? view,
            ISampleProvider provider,
            AudioOutputSession output,
            OutputRecordingSink recording,
            string displayName)
        {
            _module = module;
            Plugin = plugin;
            _view = view;
            _provider = provider;
            _output = output;
            _recording = recording;
            DisplayName = displayName;
        }

        public Vst3Plugin Plugin { get; }
        public string DisplayName { get; }
        public event EventHandler? EditorClosed;
        public bool HasEditor => _view is not null;
        public IReadOnlyList<string> Programs =>
            Plugin.ActiveProgramList?.Programs ?? Array.Empty<string>();
        public int CurrentProgram => Plugin.CurrentProgram;
        public string AudioStatus
        {
            get
            {
                if (_output.IsAsio)
                {
                    var buffer = _output.BufferFrames is { } frames
                        ? $" · búfer {frames} muestras"
                        : string.Empty;
                    return $"Audio VST3 · {_output.DeviceName} · ASIO directo{buffer} · " +
                           $"{_output.LatencyMilliseconds} ms de salida · " +
                           $"plugin {Plugin.LatencySamples} muestras";
                }

                var mode = _output.IsLowLatencyActive
                    ? $"{_output.LatencyMilliseconds} ms reales · baja latencia"
                    : $"{_output.LatencyMilliseconds} ms · WASAPI estándar";
                var raw = _output.IsRawModeActive ? " · RAW" : string.Empty;
                var plugin = Plugin.LatencySamples > 0
                    ? $" · plugin {Plugin.LatencySamples} muestras"
                    : " · plugin 0 muestras";
                var reason = _output.IsLowLatencyActive ||
                             string.IsNullOrWhiteSpace(_output.LowLatencyUnavailableReason)
                    ? string.Empty
                    : $" · {_output.LowLatencyUnavailableReason}";
                return $"Audio VST3 · {_output.DeviceName} · {mode}{raw}{plugin}{reason}";
            }
        }

        public static Vst3Runtime Load(Vst3RuntimeConfiguration configuration)
        {
            Vst3Module? module = null;
            Vst3Plugin? plugin = null;
            Vst3PluginView? view = null;
            AudioOutputSession? output = null;
            OutputRecordingSink? recording = null;
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
                plugin = module.CreatePlugin(
                    pluginClass,
                    configuration.SampleRate,
                    AudioLatencySettings.VstMaxBlockSize);
                if (!plugin.IsInstrument)
                {
                    throw new InvalidOperationException(
                        $"{configuration.Name} no se identificó como instrumento VST3.");
                }

                var provider = new DiagnosticSampleProvider(
                    new Vst3InstrumentSampleProvider(plugin));
                if (provider.WaveFormat.Channels != 2)
                {
                    throw new NotSupportedException(
                        $"{configuration.Name} ha abierto {provider.WaveFormat.Channels} canales; " +
                        "esta versión necesita una salida principal estéreo.");
                }

                view = plugin.CreateView();
                recording = new OutputRecordingSink();
                output = AudioOutputSession.Open(
                    provider,
                    configuration.OutputDeviceId,
                    [],
                    recording);
                output.Play();
                return new Vst3Runtime(
                    module,
                    plugin,
                    view,
                    provider,
                    output,
                    recording,
                    configuration.Name);
            }
            catch
            {
                output?.Dispose();
                recording?.Dispose();
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
            _editor.ClosedByUser += (_, _) =>
            {
                _editor = null;
                EditorClosed?.Invoke(this, EventArgs.Empty);
            };
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
            var replacement = AudioOutputSession.Open(_provider, deviceId, [], _recording);
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

        public void SavePreset(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("La ruta de estado VST3 está vacía.", nameof(path));
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            Plugin.SavePreset(path);
        }

        public void StartRecording(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("La ruta de grabación VST3 está vacía.", nameof(path));
            }
            _recording.Start(path, _provider.WaveFormat);
        }

        public void StopRecording() =>
            _recording.StopAsync().GetAwaiter().GetResult();

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
            _recording.Dispose();
            _view?.Dispose();
            Plugin.Dispose();
            _module.Dispose();
        }

    }

    private sealed class DiagnosticSampleProvider(ISampleProvider source) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            try
            {
                return source.Read(buffer);
            }
            catch (Exception exception)
            {
                Vst3RuntimeDiagnostics.Error("Fallo durante IAudioProcessor.Process", exception);
                throw;
            }
        }
    }

    private static class Vst3RuntimeDiagnostics
    {
        private static readonly object Sync = new();
        private static string? _path;

        public static void Initialize(string path)
        {
            _path = path;
            Info($"Proceso VST3 iniciado · .NET {Environment.Version} · {Environment.OSVersion}");
        }

        public static void Info(string message) => Write("INFO", message, null);

        public static void Error(string message, Exception? exception) =>
            Write("ERROR", message, exception);

        private static void Write(string level, string message, Exception? exception)
        {
            var path = _path;
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var summary = exception is null
                    ? message
                    : $"{message} · {exception.GetType().FullName}: {exception.Message}";
                var entry = $"{DateTimeOffset.Now:O} [{level}] {summary}{Environment.NewLine}";
                if (exception is not null)
                {
                    entry += exception + Environment.NewLine;
                }

                lock (Sync)
                {
                    File.AppendAllText(path, entry, Encoding.UTF8);
                }
            }
            catch
            {
                // El diagnóstico nunca debe provocar otro fallo en el hilo de audio.
            }
        }
    }
}
