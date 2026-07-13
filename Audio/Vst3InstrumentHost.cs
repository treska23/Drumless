using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Audio;

internal sealed class Vst3InstrumentHost : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(45);
    private readonly object _sync = new();
    private Process? _process;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private string? _configurationPath;
    private string? _diagnosticPath;
    private bool _stopping;
    private bool _hasEditor;

    public event EventHandler<string>? Exited;

    public bool IsLoaded
    {
        get
        {
            lock (_sync)
            {
                return IsProcessRunning(_process) && _writer is not null;
            }
        }
    }

    public string? DisplayName { get; private set; }
    public IReadOnlyList<string> Programs { get; private set; } = Array.Empty<string>();
    public int CurrentProgram { get; private set; } = -1;
    public string AudioStatus { get; private set; } = "Audio VST3 no inicializado";

    public async Task LoadAsync(
        Vst3InstrumentItem instrument,
        int sampleRate,
        string? outputDeviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Unload();

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("No se pudo localizar el ejecutable de Drum Practice Studio.");
        }

        var pipeName = $"DrumPracticeStudio.Vst3.{Guid.NewGuid():N}";
        var configurationPath = Path.Combine(
            Path.GetTempPath(),
            $"DrumPracticeStudio.Vst3.{Guid.NewGuid():N}.json");
        var diagnosticPath = CreateDiagnosticPath();
        var configuration = new Vst3RuntimeConfiguration(
            instrument.Module.Path,
            instrument.Module.Name,
            instrument.PluginClass.ClassId,
            instrument.PluginClass.Category,
            instrument.PluginClass.Name,
            instrument.PluginClass.Vendor,
            instrument.PluginClass.Version,
            instrument.PluginClass.SdkVersion,
            instrument.PluginClass.SubCategories,
            sampleRate,
            outputDeviceId,
            diagnosticPath);
        await File.WriteAllTextAsync(
            configurationPath,
            JsonSerializer.Serialize(configuration),
            cancellationToken);

        var pipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(Vst3RuntimeProtocol.Argument);
        startInfo.ArgumentList.Add(configurationPath);
        startInfo.ArgumentList.Add(pipeName);

        Process? process = null;
        StreamWriter? commandWriter = null;
        try
        {
            process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("No se pudo iniciar el motor VST3 aislado.");
            try
            {
                process.PriorityClass = ProcessPriorityClass.AboveNormal;
            }
            catch
            {
                // Windows puede denegar el cambio de prioridad; el hilo de audio aún usa MMCSS.
            }
            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;

            lock (_sync)
            {
                _process = process;
                _pipe = pipe;
                _configurationPath = configurationPath;
                _diagnosticPath = diagnosticPath;
                _stopping = false;
                DisplayName = instrument.DisplayName;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(StartupTimeout);
            await pipe.WaitForConnectionAsync(timeout.Token);

            var reader = new StreamReader(
                pipe,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1_024,
                leaveOpen: true);
            commandWriter = new StreamWriter(
                pipe,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                bufferSize: 1_024,
                leaveOpen: true) { AutoFlush = true };
            var responseLine = await reader.ReadLineAsync(timeout.Token);
            var response = responseLine is null
                ? null
                : JsonSerializer.Deserialize<Vst3RuntimeResponse>(responseLine);
            reader.Dispose();
            if (response is null || !response.Ready)
            {
                throw new InvalidOperationException(
                    (response?.Message ?? "El motor VST3 se cerró durante la carga.") +
                    $" Registro: {diagnosticPath}");
            }

            lock (_sync)
            {
                _writer = commandWriter;
                _hasEditor = response.HasEditor;
                Programs = response.Programs ?? Array.Empty<string>();
                CurrentProgram = response.CurrentProgram;
                AudioStatus = response.AudioStatus ?? "Audio VST3 preparado";
            }
            commandWriter = null;
            TryDeleteConfiguration();
        }
        catch
        {
            if (process is not null)
            {
                try
                {
                    process.Exited -= OnProcessExited;
                }
                catch
                {
                    // El evento puede haberse disparado y liberado ya el proceso.
                }
            }
            commandWriter?.Dispose();
            Unload();
            pipe.Dispose();
            TryDelete(configurationPath);
            throw;
        }
    }

    public void SendNoteOn(int note, int velocity, int channel) =>
        Send(new Vst3RuntimeCommand(
            "NoteOn",
            Math.Clamp(note, 0, 127),
            Math.Clamp(velocity, 1, 127),
            Math.Clamp(channel - 1, 0, 15)));

    public void SendNoteOff(int note, int velocity, int channel) =>
        Send(new Vst3RuntimeCommand(
            "NoteOff",
            Math.Clamp(note, 0, 127),
            Math.Clamp(velocity, 0, 127),
            Math.Clamp(channel - 1, 0, 15)));

    public void SendControlChange(int controller, int value) =>
        Send(new Vst3RuntimeCommand(
            "ControlChange",
            Math.Clamp(controller, 0, 127),
            Math.Clamp(value, 0, 127)));

    public void Panic() => Send(new Vst3RuntimeCommand("Panic"));

    public void SetOutputDevice(string? deviceId) =>
        Send(new Vst3RuntimeCommand("SetOutput", Text: deviceId));

    public void SelectProgram(int programIndex)
    {
        if (programIndex < 0 || programIndex >= Programs.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(programIndex));
        }

        CurrentProgram = programIndex;
        Send(new Vst3RuntimeCommand("ProgramChange", programIndex));
    }

    public void LoadPreset(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Send(new Vst3RuntimeCommand("LoadPreset", Text: path));
    }

    public bool OpenEditor()
    {
        lock (_sync)
        {
            if (!_hasEditor || !IsProcessRunning(_process) || _writer is null)
            {
                return false;
            }
        }

        Send(new Vst3RuntimeCommand("OpenEditor"));
        return true;
    }

    public void Unload()
    {
        Process? process;
        NamedPipeServerStream? pipe;
        StreamWriter? writer;
        lock (_sync)
        {
            _stopping = true;
            process = _process;
            pipe = _pipe;
            writer = _writer;
            _process = null;
            _pipe = null;
            _writer = null;
            _diagnosticPath = null;
            _hasEditor = false;
            DisplayName = null;
            Programs = Array.Empty<string>();
            CurrentProgram = -1;
            AudioStatus = "Audio VST3 no inicializado";
        }

        try
        {
            writer?.WriteLine(JsonSerializer.Serialize(new Vst3RuntimeCommand("Stop")));
        }
        catch
        {
            // El motor puede haberse cerrado antes de recibir Stop.
        }
        writer?.Dispose();
        pipe?.Dispose();

        if (process is not null)
        {
            process.Exited -= OnProcessExited;
            try
            {
                if (!process.HasExited && !process.WaitForExit(2_000))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // El proceso puede haberse cerrado mientras se comprobaba.
            }
            process.Dispose();
        }

        TryDeleteConfiguration();
        lock (_sync)
        {
            _stopping = false;
        }
    }

    public void Dispose() => Unload();

    private void Send(Vst3RuntimeCommand command)
    {
        string? failure = null;
        lock (_sync)
        {
            if (!IsProcessRunning(_process) || _writer is null)
            {
                return;
            }

            try
            {
                _writer.WriteLine(JsonSerializer.Serialize(command));
            }
            catch (Exception exception)
            {
                failure = exception.Message;
            }
        }

        if (failure is not null)
        {
            Exited?.Invoke(this, $"El motor VST3 dejó de responder: {failure}");
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var notify = false;
        Process? exitedProcess = null;
        string? diagnosticPath = null;
        int? exitCode = null;
        lock (_sync)
        {
            if (ReferenceEquals(sender, _process) && !_stopping)
            {
                try
                {
                    _writer?.Dispose();
                    _pipe?.Dispose();
                }
                catch
                {
                    // La tubería ya está rota cuando el proceso nativo termina abruptamente.
                }
                exitedProcess = _process;
                diagnosticPath = _diagnosticPath;
                _diagnosticPath = null;
                try
                {
                    exitCode = exitedProcess?.ExitCode;
                }
                catch
                {
                    // El código puede no estar disponible durante una carrera de cierre.
                }
                _writer = null;
                _pipe = null;
                _process = null;
                _hasEditor = false;
                AudioStatus = "Audio VST3 detenido";
                notify = true;
            }
        }

        exitedProcess?.Dispose();
        TryDeleteConfiguration();
        if (notify)
        {
            var diagnostic = ReadDiagnosticSummary(diagnosticPath);
            var exitLabel = exitCode is null ? string.Empty : $" (código {exitCode})";
            var detail = string.IsNullOrWhiteSpace(diagnostic)
                ? string.Empty
                : $" Detalle: {diagnostic}";
            var logLocation = string.IsNullOrWhiteSpace(diagnosticPath)
                ? string.Empty
                : $" Registro: {diagnosticPath}";
            Exited?.Invoke(
                this,
                $"El instrumento VST3 se cerró por un fallo interno{exitLabel}." + detail + logLocation +
                " La batería queda silenciada; Drumless no cambiará al kit interno sin que tú lo pidas.");
        }
    }

    private static string CreateDiagnosticPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DrumPracticeStudio",
            "Vst3Logs");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var oldLog in Directory.GetFiles(directory, "vst3-*.log")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(20))
            {
                TryDelete(oldLog);
            }
        }
        catch
        {
            // La rotación de diagnósticos no debe impedir cargar el instrumento.
        }
        return Path.Combine(
            directory,
            $"vst3-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
    }

    private static string? ReadDiagnosticSummary(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadLines(path)
                .LastOrDefault(line => line.Contains("[ERROR]", StringComparison.Ordinal));
        }
        catch
        {
            return null;
        }
    }

    private void TryDeleteConfiguration()
    {
        string? path;
        lock (_sync)
        {
            path = _configurationPath;
            _configurationPath = null;
        }
        if (path is not null)
        {
            TryDelete(path);
        }
    }

    private static bool IsProcessRunning(Process? process)
    {
        if (process is null)
        {
            return false;
        }
        try
        {
            return !process.HasExited;
        }
        catch
        {
            return false;
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
            // La limpieza temporal no debe impedir cerrar el motor.
        }
    }
}
