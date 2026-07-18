using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Audio;

internal sealed class IsolatedVst3EffectProcessor : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(35);
    private readonly Process _process;
    private readonly BinaryWriter _writer;
    private readonly BinaryReader _reader;
    private readonly Channel<AudioPacket> _input;
    private readonly ConcurrentQueue<AudioPacket> _output = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _bridge;
    private int _disposed;
    private int _pipelineFrames;
    private string? _failure;

    private IsolatedVst3EffectProcessor(
        Process process,
        BinaryWriter writer,
        BinaryReader reader,
        Vst3EffectReference reference,
        uint pluginLatencySamples)
    {
        _process = process;
        _writer = writer;
        _reader = reader;
        Reference = reference;
        PluginLatencySamples = pluginLatencySamples;
        _input = Channel.CreateBounded<AudioPacket>(new BoundedChannelOptions(3)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        _bridge = Task.Run(BridgeAsync);
    }

    public Vst3EffectReference Reference { get; }
    public uint PluginLatencySamples { get; }
    public uint TotalLatencySamples =>
        PluginLatencySamples + (uint)Math.Max(0, Volatile.Read(ref _pipelineFrames));
    public string? Failure => Volatile.Read(ref _failure);
    public bool IsAvailable => Failure is null && Volatile.Read(ref _disposed) == 0;

    public static IsolatedVst3EffectProcessor Start(
        Vst3EffectReference reference,
        int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
        {
            throw new InvalidOperationException("No se encontró el ejecutable para aislar el VST3.");
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            $"DrumPracticeStudio.Effect.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var configurationPath = Path.Combine(root, "configuration.json");
        var readyPath = Path.Combine(root, "ready.json");
        var diagnosticPath = Path.Combine(AppPaths.Vst3Logs, $"effect-{Guid.NewGuid():N}.log");
        Directory.CreateDirectory(AppPaths.Vst3Logs);
        var configuration = new Vst3EffectRuntimeConfiguration(
            reference,
            sampleRate,
            AudioLatencySettings.VstMaxBlockSize,
            readyPath,
            diagnosticPath);
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(configuration));

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(Vst3EffectRuntimeProtocol.Argument);
        startInfo.ArgumentList.Add(configurationPath);
        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException("No se pudo iniciar el efecto VST3 aislado.");
        try
        {
            process.PriorityClass = ProcessPriorityClass.AboveNormal;
        }
        catch
        {
        }

        try
        {
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(readyPath))
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"El efecto VST3 terminó al iniciar (código {process.ExitCode}).");
                }
                if (deadline.Elapsed >= StartupTimeout)
                {
                    throw new TimeoutException("El efecto VST3 no respondió durante la carga.");
                }
                Thread.Sleep(40);
            }

            var ready = JsonSerializer.Deserialize<Vst3EffectRuntimeReady>(
                            File.ReadAllText(readyPath))
                        ?? throw new InvalidDataException("Respuesta VST3 vacía.");
            if (!ready.Ready)
            {
                throw new InvalidOperationException(
                    $"{ready.Message} Registro: {diagnosticPath}");
            }
            TryDeleteDirectory(root);
            return new IsolatedVst3EffectProcessor(
                process,
                new BinaryWriter(process.StandardInput.BaseStream),
                new BinaryReader(process.StandardOutput.BaseStream),
                reference,
                ready.LatencySamples);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            process.Dispose();
            TryDeleteDirectory(root);
            throw;
        }
    }

    public void ProcessMono(Span<float> samples, float wetMix)
    {
        if (!IsAvailable || samples.IsEmpty)
        {
            return;
        }

        MixAvailableOutput(samples, channels: 1, wetMix);

        var buffer = ArrayPool<float>.Shared.Rent(samples.Length * 2);
        for (var frame = 0; frame < samples.Length; frame++)
        {
            buffer[frame * 2] = samples[frame];
            buffer[(frame * 2) + 1] = samples[frame];
        }
        Submit(buffer, samples.Length);
    }

    public void ProcessStereo(Span<float> samples, float wetMix)
    {
        if (!IsAvailable || samples.Length < 2)
        {
            return;
        }
        var frames = samples.Length / 2;
        MixAvailableOutput(samples[..(frames * 2)], channels: 2, wetMix);
        var buffer = ArrayPool<float>.Shared.Rent(frames * 2);
        samples[..(frames * 2)].CopyTo(buffer);
        Submit(buffer, frames);
    }

    private void MixAvailableOutput(Span<float> samples, int channels, float wetMix)
    {
        if (_output.TryDequeue(out var processed))
        {
            try
            {
                if (processed.Frames * channels == samples.Length)
                {
                    var dryMix = 1f - Math.Clamp(wetMix, 0f, 1f);
                    for (var frame = 0; frame < processed.Frames; frame++)
                    {
                        if (channels == 1)
                        {
                            var wet = (processed.Buffer[frame * 2] +
                                       processed.Buffer[(frame * 2) + 1]) * 0.5f;
                            samples[frame] = Math.Clamp(
                                (samples[frame] * dryMix) + (wet * wetMix),
                                -1f,
                                1f);
                        }
                        else
                        {
                            for (var channel = 0; channel < 2; channel++)
                            {
                                var index = (frame * 2) + channel;
                                samples[index] = Math.Clamp(
                                    (samples[index] * dryMix) +
                                    (processed.Buffer[index] * wetMix),
                                    -1f,
                                    1f);
                            }
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(processed.Buffer);
            }
        }
    }

    private void Submit(float[] buffer, int frames)
    {
        Volatile.Write(ref _pipelineFrames, frames);
        if (!_input.Writer.TryWrite(new AudioPacket(buffer, frames)))
        {
            ArrayPool<float>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _input.Writer.TryComplete();
        _cancellation.Cancel();
        try
        {
            _writer.Write(0);
            _writer.Flush();
        }
        catch
        {
        }
        try
        {
            _bridge.Wait(1_000);
        }
        catch
        {
        }
        while (_output.TryDequeue(out var packet))
        {
            ArrayPool<float>.Shared.Return(packet.Buffer);
        }
        _writer.Dispose();
        _reader.Dispose();
        try
        {
            if (!_process.HasExited && !_process.WaitForExit(1_000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        _process.Dispose();
        _cancellation.Dispose();
    }

    private async Task BridgeAsync()
    {
        try
        {
            await foreach (var packet in _input.Reader.ReadAllAsync(_cancellation.Token))
            {
                try
                {
                    _writer.Write(packet.Frames);
                    for (var index = 0; index < packet.Frames * 2; index++)
                    {
                        _writer.Write(packet.Buffer[index]);
                    }
                    _writer.Flush();
                    var outputFrames = _reader.ReadInt32();
                    var outputBuffer = ArrayPool<float>.Shared.Rent(outputFrames * 2);
                    for (var index = 0; index < outputFrames * 2; index++)
                    {
                        outputBuffer[index] = _reader.ReadSingle();
                    }
                    _output.Enqueue(new AudioPacket(outputBuffer, outputFrames));
                    while (_output.Count > 2 && _output.TryDequeue(out var stale))
                    {
                        ArrayPool<float>.Shared.Return(stale.Buffer);
                    }
                }
                finally
                {
                    ArrayPool<float>.Shared.Return(packet.Buffer);
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Volatile.Write(
                ref _failure,
                $"{Reference.Name} quedó en bypass: {exception.Message}");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private sealed record AudioPacket(float[] Buffer, int Frames);
}
