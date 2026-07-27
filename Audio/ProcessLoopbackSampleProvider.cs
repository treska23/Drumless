using System.Diagnostics;
using System.Runtime.InteropServices;
using DrumPracticeStudio.Services;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class ProcessLoopbackSampleProvider : ISampleProvider, IDisposable
{
    private readonly Process _process;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _sampleProvider;
    private readonly CancellationTokenSource _pumpCancellation = new();
    private readonly Task _pumpTask;
    private int _peakBits;
    private int _disposeStarted;

    private ProcessLoopbackSampleProvider(Process process, WaveFormat format)
    {
        _process = process;
        _buffer = new BufferedWaveProvider(format, TimeSpan.FromSeconds(2))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };
        _sampleProvider = _buffer.ToSampleProvider();
        _pumpTask = PumpAudioAsync(_pumpCancellation.Token);
    }

    public WaveFormat WaveFormat => _sampleProvider.WaveFormat;

    public static async Task<ProcessLoopbackSampleProvider> StartAsync(
        uint rootProcessId,
        WaveFormat format)
    {
        if (rootProcessId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rootProcessId),
                "El proceso raíz de WebView2 no es válido.");
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException(
                "No se encontró el ejecutable para iniciar el capturador de YouTube.");
        }

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add(ProcessLoopbackCaptureProtocol.Argument);
        startInfo.ArgumentList.Add(rootProcessId.ToString());

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "Windows no pudo iniciar el capturador aislado de YouTube.");
            }

            var response = await process.StandardError
                .ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds(10))
                .ConfigureAwait(false);
            if (!string.Equals(
                    response,
                    ProcessLoopbackCaptureProtocol.ReadyResponse,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    ProcessLoopbackCaptureProtocol.DecodeError(response) ??
                    "El capturador aislado de YouTube terminó antes de estar preparado.");
            }

            return new ProcessLoopbackSampleProvider(process, format);
        }
        catch
        {
            TryStopProcess(process);
            process.Dispose();
            throw;
        }
    }

    public int Read(Span<float> buffer) =>
        _sampleProvider.Read(buffer);

    public float TakePeak() =>
        BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _peakBits, 0));

    private async Task PumpAudioAsync(CancellationToken cancellationToken)
    {
        var bytes = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await _process.StandardOutput.BaseStream.ReadAsync(
                    bytes,
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                var packet = bytes.AsSpan(0, read);
                _buffer.AddSamples(packet);
                UpdatePeak(packet);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // El proceso auxiliar ya no está disponible; el mezclador recibe silencio.
        }
        catch (ObjectDisposedException)
        {
            // El cierre asíncrono puede liberar la tubería mientras termina una lectura.
        }
        catch (InvalidOperationException)
        {
            // El proceso o una de sus tuberías ya se cerraron.
        }
    }

    private void UpdatePeak(ReadOnlySpan<byte> packet)
    {
        var alignedLength = packet.Length - packet.Length % sizeof(float);
        if (alignedLength == 0)
        {
            return;
        }

        var samples = MemoryMarshal.Cast<byte, float>(packet[..alignedLength]);
        var peak = 0f;
        foreach (var sample in samples)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        var peakBits = BitConverter.SingleToInt32Bits(peak);
        while (true)
        {
            var currentBits = Volatile.Read(ref _peakBits);
            if (BitConverter.Int32BitsToSingle(currentBits) >= peak ||
                Interlocked.CompareExchange(ref _peakBits, peakBits, currentBits) == currentBits)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _pumpCancellation.Cancel();

        // Cerrar una captura por proceso puede tardar si Windows mantiene una lectura de la tubería
        // pendiente. Nunca esperamos esa operación desde el hilo WPF: el audio se retira primero del
        // mezclador y los recursos del proceso se limpian en segundo plano.
        _ = Task.Run(CleanupAsync);
    }

    private async Task CleanupAsync()
    {
        try
        {
            _process.StandardInput.Close();
        }
        catch
        {
        }

        TryStopProcess(_process);

        try
        {
            await _pumpTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                _process.Dispose();
            }
            catch
            {
            }

            _pumpCancellation.Dispose();
            _buffer.ClearBuffer();
        }
    }

    private static void TryStopProcess(Process process)
    {
        try
        {
            if (!process.HasExited && !process.WaitForExit(1_000))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1_000);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }
}
