using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Audio;

internal sealed class IsolatedVst3EffectProcessor : IDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan AudioBlockTimeout = TimeSpan.FromSeconds(2);
    private readonly Process _process;
    private readonly Stream _bridgeStream;
    private readonly BinaryWriter _writer;
    private readonly BinaryReader _reader;
    private readonly Channel<BridgeRequest> _input;
    private readonly ConcurrentQueue<AudioPacket> _output = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _bridge;
    private readonly Timer _watchdog;
    private int _disposed;
    private int _pipelineFrames;
    private int _suspiciousOutputBlocks;
    private string? _failure;

    private IsolatedVst3EffectProcessor(
        Process process,
        Stream bridgeStream,
        Vst3EffectReference reference,
        uint pluginLatencySamples,
        bool hasEditor)
    {
        _process = process;
        _bridgeStream = bridgeStream;
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _writer = new BinaryWriter(bridgeStream, utf8, leaveOpen: true);
        _reader = new BinaryReader(bridgeStream, utf8, leaveOpen: true);
        Reference = reference;
        PluginLatencySamples = pluginLatencySamples;
        HasEditor = hasEditor;
        _input = Channel.CreateBounded<BridgeRequest>(new BoundedChannelOptions(4)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
        _watchdog = new Timer(OnAudioBlockTimeout);
        _bridge = Task.Run(BridgeAsync);
    }

    public Vst3EffectReference Reference { get; }
    public uint PluginLatencySamples { get; }
    public bool HasEditor { get; }
    public uint TotalLatencySamples =>
        PluginLatencySamples + (uint)Math.Max(0, Volatile.Read(ref _pipelineFrames));
    public string? Failure => Volatile.Read(ref _failure);
    public bool IsAvailable => Failure is null && Volatile.Read(ref _disposed) == 0;

    public static IsolatedVst3EffectProcessor Start(
        Vst3EffectReference reference,
        int sampleRate,
        string slotId)
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
        var pipeName = $"DrumPracticeStudio.Effect.Audio.{Guid.NewGuid():N}";
        var bridgePipe = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var diagnosticPath = Path.Combine(AppPaths.Vst3Logs, $"effect-{Guid.NewGuid():N}.log");
        Directory.CreateDirectory(AppPaths.Vst3Logs);
        var configuration = new Vst3EffectRuntimeConfiguration(
            reference,
            sampleRate,
            AudioLatencySettings.VstMaxBlockSize,
            readyPath,
            diagnosticPath,
            GetAutomaticStatePath(slotId, reference),
            pipeName);
        File.WriteAllText(configurationPath, JsonSerializer.Serialize(configuration));

        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
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
            bridgePipe.WaitForConnectionAsync()
                .WaitAsync(StartupTimeout)
                .GetAwaiter()
                .GetResult();
            TryDeleteDirectory(root);
            return new IsolatedVst3EffectProcessor(
                process,
                bridgePipe,
                reference,
                ready.LatencySamples,
                ready.HasEditor);
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
            bridgePipe.Dispose();
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

        var buffer = ArrayPool<float>.Shared.Rent(samples.Length * 2);
        for (var frame = 0; frame < samples.Length; frame++)
        {
            // Submit the current dry input. Feeding the previous wet block back into the plug-in
            // creates an unintended feedback loop, which is especially dangerous with amp sims.
            buffer[frame * 2] = samples[frame];
            buffer[(frame * 2) + 1] = samples[frame];
        }
        MixAvailableOutput(samples, channels: 1, wetMix);
        Submit(buffer, samples.Length);
    }

    public void ProcessStereo(Span<float> samples, float wetMix)
    {
        if (!IsAvailable || samples.Length < 2)
        {
            return;
        }
        var frames = samples.Length / 2;
        var buffer = ArrayPool<float>.Shared.Rent(frames * 2);
        samples[..(frames * 2)].CopyTo(buffer);
        MixAvailableOutput(samples[..(frames * 2)], channels: 2, wetMix);
        Submit(buffer, frames);
    }

    public async Task<Vst3EffectEditorResult> OpenEditorAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return new Vst3EffectEditorResult(
                false,
                Failure ?? $"{Reference.Name} no está activo.");
        }
        if (!HasEditor)
        {
            return new Vst3EffectEditorResult(
                false,
                $"{Reference.Name} no proporciona una interfaz VST3 compatible.");
        }

        var completion = new TaskCompletionSource<Vst3EffectEditorResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _input.Writer.WriteAsync(
                new ControlRequest(Vst3EffectRuntimeProtocol.OpenEditorCommand, completion),
                cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
        }
        catch (Exception exception) when (exception is
            ChannelClosedException or
            TimeoutException or
            OperationCanceledException)
        {
            return new Vst3EffectEditorResult(
                false,
                $"No se pudo abrir la interfaz de {Reference.Name}: {exception.Message}");
        }
    }

    private void MixAvailableOutput(Span<float> samples, int channels, float wetMix)
    {
        if (_output.TryDequeue(out var processed))
        {
            try
            {
                if (processed.Frames * channels == samples.Length)
                {
                    if (!ValidateOutput(samples, processed))
                    {
                        return;
                    }
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

    private bool ValidateOutput(ReadOnlySpan<float> dry, AudioPacket processed)
    {
        double drySquares = 0d;
        double wetSquares = 0d;
        var wetSamples = processed.Frames * 2;
        for (var index = 0; index < dry.Length; index++)
        {
            var sample = dry[index];
            if (float.IsFinite(sample))
            {
                drySquares += sample * sample;
            }
        }
        for (var index = 0; index < wetSamples; index++)
        {
            var sample = processed.Buffer[index];
            if (!float.IsFinite(sample) || MathF.Abs(sample) > 8f)
            {
                Volatile.Write(
                    ref _failure,
                    $"{Reference.Name} quedó en bypass de seguridad: produjo audio no válido o fuera de rango.");
                return false;
            }
            wetSquares += sample * sample;
        }

        var dryRms = Math.Sqrt(drySquares / Math.Max(1, dry.Length));
        var wetRms = Math.Sqrt(wetSquares / Math.Max(1, wetSamples));
        var suspicious = dryRms < 0.001d && wetRms > 0.7d;
        _suspiciousOutputBlocks = suspicious ? _suspiciousOutputBlocks + 1 : 0;
        if (_suspiciousOutputBlocks < 3)
        {
            return true;
        }

        Volatile.Write(
            ref _failure,
            $"{Reference.Name} quedó en bypass de seguridad: detectada una salida sostenida muy alta sin señal de entrada.");
        return false;
    }

    private void Submit(float[] buffer, int frames)
    {
        Volatile.Write(ref _pipelineFrames, frames);
        if (!_input.Writer.TryWrite(new AudioRequest(buffer, frames)))
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
        var bridgeStopped = false;
        try
        {
            bridgeStopped = _bridge.Wait(1_000);
        }
        catch
        {
        }
        if (!bridgeStopped)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
                bridgeStopped = _bridge.Wait(1_000);
            }
            catch
            {
            }
        }
        while (_output.TryDequeue(out var packet))
        {
            ArrayPool<float>.Shared.Return(packet.Buffer);
        }
        // Do not dispose BinaryWriter here: its Dispose() flushes and therefore deliberately throws
        // when a plug-in has already closed its pipe. Closing the owned stream is sufficient and
        // avoids surfacing a first-chance broken-pipe exception while replacing a slot.
        DisposeIgnoringErrors(_bridgeStream);
        try
        {
            if (!_process.HasExited && !_process.WaitForExit(3_000))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        _process.Dispose();
        _watchdog.Dispose();
        _cancellation.Dispose();
    }

    internal static void DisposeIgnoringErrors(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception exception) when (exception is
            IOException or
            ObjectDisposedException or
            InvalidOperationException)
        {
        }
    }

    private async Task BridgeAsync()
    {
        try
        {
            await foreach (var request in _input.Reader.ReadAllAsync(_cancellation.Token))
            {
                switch (request)
                {
                    case AudioRequest audio:
                        ProcessAudioRequest(audio);
                        break;
                    case ControlRequest control:
                        ProcessControlRequest(control);
                        break;
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
        finally
        {
            while (_input.Reader.TryRead(out var pending))
            {
                switch (pending)
                {
                    case AudioRequest audio:
                        ArrayPool<float>.Shared.Return(audio.Buffer);
                        break;
                    case ControlRequest control:
                        control.Completion.TrySetResult(new Vst3EffectEditorResult(
                            false,
                            $"{Reference.Name} se cerró antes de abrir su interfaz."));
                        break;
                }
            }
        }
    }

    private void ProcessAudioRequest(AudioRequest packet)
    {
        try
        {
            _watchdog.Change(AudioBlockTimeout, Timeout.InfiniteTimeSpan);
            _writer.Write(packet.Frames);
            _writer.Write(MemoryMarshal.AsBytes(
                packet.Buffer.AsSpan(0, packet.Frames * 2)));
            _writer.Flush();
            var outputFrames = _reader.ReadInt32();
            if (outputFrames != packet.Frames)
            {
                throw new InvalidDataException(
                    $"{Reference.Name} devolvió {outputFrames} frames para un bloque de {packet.Frames}.");
            }
            var outputBuffer = ArrayPool<float>.Shared.Rent(outputFrames * 2);
            _reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(
                outputBuffer.AsSpan(0, outputFrames * 2)));
            _output.Enqueue(new AudioPacket(outputBuffer, outputFrames));
            while (_output.Count > 2 && _output.TryDequeue(out var stale))
            {
                ArrayPool<float>.Shared.Return(stale.Buffer);
            }
        }
        finally
        {
            _watchdog.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            ArrayPool<float>.Shared.Return(packet.Buffer);
        }
    }

    private void OnAudioBlockTimeout(object? state)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }
        Volatile.Write(
            ref _failure,
            $"{Reference.Name} quedó en bypass de seguridad porque dejó de responder al procesar audio.");
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private void ProcessControlRequest(ControlRequest request)
    {
        try
        {
            _writer.Write(request.Command);
            _writer.Flush();
            var succeeded = _reader.ReadBoolean();
            var message = _reader.ReadString();
            request.Completion.TrySetResult(new Vst3EffectEditorResult(succeeded, message));
        }
        catch (Exception exception)
        {
            request.Completion.TrySetResult(new Vst3EffectEditorResult(
                false,
                $"No se pudo controlar {Reference.Name}: {exception.Message}"));
            throw;
        }
    }

    private static string GetAutomaticStatePath(
        string slotId,
        Vst3EffectReference reference)
    {
        var identity = Encoding.UTF8.GetBytes(
            $"{reference.ModulePath}|{reference.ClassId}|{reference.PresetPath}");
        var fingerprint = Convert.ToHexString(SHA256.HashData(identity))[..16];
        var safeSlotId = string.Concat(slotId.Where(char.IsLetterOrDigit));
        if (string.IsNullOrWhiteSpace(safeSlotId))
        {
            safeSlotId = "slot";
        }
        else if (safeSlotId.Length > 64)
        {
            safeSlotId = safeSlotId[..64];
        }
        return Path.Combine(
            AppPaths.VstStates,
            $"effect-{safeSlotId}-{fingerprint}.vstpreset");
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

    private abstract record BridgeRequest;
    private sealed record AudioRequest(float[] Buffer, int Frames) : BridgeRequest;
    private sealed record ControlRequest(
        int Command,
        TaskCompletionSource<Vst3EffectEditorResult> Completion) : BridgeRequest;
    private sealed record AudioPacket(float[] Buffer, int Frames);
}

internal sealed record Vst3EffectEditorResult(bool Succeeded, string Message);
