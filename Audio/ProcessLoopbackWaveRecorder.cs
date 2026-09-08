using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

/// <summary>
/// Captura el audio renderizado por un árbol de procesos sin reinyectarlo en la salida.
/// Se usa únicamente para incorporar fuentes que suenan fuera del mezclador de Drumless
/// (actualmente WebView2/YouTube) a la grabación final.
/// </summary>
internal sealed class ProcessLoopbackWaveRecorder : IDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int CaptureBufferMilliseconds = 10;
    private const int SilenceChunkFrames = 4_096;

    private readonly object _gate = new();
    private readonly WasapiRecorder _recorder;
    private readonly WaveFileWriter _writer;
    private readonly WaveFormat _format;
    private readonly byte[] _silence;
    private long _startTimestamp;
    private long _writtenFrames;
    private int _stopStarted;
    private int _hasCapturedAudio;
    private bool _started;
    private bool _stopped;
    private bool _disposed;
    private Exception? _failure;

    private ProcessLoopbackWaveRecorder(
        WasapiRecorder recorder,
        WaveFileWriter writer,
        WaveFormat format,
        string path)
    {
        _recorder = recorder;
        _writer = writer;
        _format = format;
        Path = path;
        _silence = new byte[SilenceChunkFrames * format.BlockAlign];
        _recorder.DataAvailable += OnDataAvailable;
    }

    public string Path { get; }
    public Exception? Failure
    {
        get
        {
            lock (_gate)
            {
                return _failure;
            }
        }
    }

    public bool HasCapturedAudio => Volatile.Read(ref _hasCapturedAudio) != 0;

    public static async Task<ProcessLoopbackWaveRecorder> PrepareAsync(
        uint rootProcessId,
        string path,
        CancellationToken cancellationToken = default)
    {
        if (rootProcessId == 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rootProcessId),
                "El proceso de WebView2 no es válido para grabar YouTube.");
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var resolvedPath = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(resolvedPath)!);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        WasapiRecorder? recorder = null;
        WaveFileWriter? writer = null;
        try
        {
            recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback(
                    rootProcessId,
                    ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(format)
                .WithBufferLength(CaptureBufferMilliseconds)
                .WithMmcssThreadPriority("Audio")
                .BuildAsync()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            writer = new WaveFileWriter(resolvedPath, format);
            return new ProcessLoopbackWaveRecorder(recorder, writer, format, resolvedPath);
        }
        catch
        {
            writer?.Dispose();
            recorder?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Inicia la captura. Si se proporciona el timestamp de inicio de la toma principal,
    /// la pista de YouTube conserva el mismo origen temporal y rellena con silencio cualquier
    /// tramo anterior a la creación de la captura.
    /// </summary>
    public void Start(long timelineStartTimestamp = 0)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started || _stopped)
            {
                throw new InvalidOperationException("La captura de YouTube ya se inició o terminó.");
            }
            _writtenFrames = 0;
            _startTimestamp = timelineStartTimestamp > 0
                ? timelineStartTimestamp
                : Stopwatch.GetTimestamp();
            _started = true;
        }

        try
        {
            _recorder.StartRecording();
        }
        catch
        {
            lock (_gate)
            {
                _started = false;
            }
            throw;
        }
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        lock (_gate)
        {
            if (!_started || _stopped || _disposed)
            {
                return;
            }

            try
            {
                var elapsedFrames = GetElapsedFrames();
                var packetFrames = buffer.Length / _format.BlockAlign;
                var packetStartFrame = Math.Max(0L, elapsedFrames - packetFrames);
                if (packetStartFrame > _writtenFrames)
                {
                    WriteSilence(packetStartFrame - _writtenFrames);
                }

                if (buffer.IsEmpty || flags.HasFlag(AudioClientBufferFlags.Silent))
                {
                    if (packetFrames > 0)
                    {
                        WriteSilence(packetFrames);
                    }
                    return;
                }

                var overlapFrames = Math.Max(0L, _writtenFrames - packetStartFrame);
                if (overlapFrames >= packetFrames)
                {
                    return;
                }

                var skipBytes = checked((int)overlapFrames * _format.BlockAlign);
                var payload = buffer[skipBytes..];
                if (!payload.IsEmpty)
                {
                    _writer.Write(payload);
                    _writtenFrames += payload.Length / _format.BlockAlign;
                    Interlocked.Exchange(ref _hasCapturedAudio, 1);
                }
            }
            catch (Exception exception) when (exception is
                IOException or
                InvalidOperationException or
                ObjectDisposedException or
                ArgumentException)
            {
                _failure ??= exception;
            }
        }
    }

    public void Stop()
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
        {
            return;
        }

        try
        {
            if (_started)
            {
                _recorder.StopRecording();
            }
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            ObjectDisposedException)
        {
            lock (_gate)
            {
                _failure ??= exception;
            }
        }

        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            if (_started)
            {
                try
                {
                    var elapsedFrames = GetElapsedFrames();
                    if (elapsedFrames > _writtenFrames)
                    {
                        WriteSilence(elapsedFrames - _writtenFrames);
                    }
                    _writer.Flush();
                }
                catch (Exception exception) when (exception is
                    IOException or
                    InvalidOperationException or
                    ObjectDisposedException)
                {
                    _failure ??= exception;
                }
            }

            _started = false;
            _stopped = true;
        }
    }

    private long GetElapsedFrames()
    {
        if (_startTimestamp == 0)
        {
            return 0;
        }
        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
        return Math.Max(0L, (long)Math.Round(elapsed.TotalSeconds * SampleRate));
    }

    private void WriteSilence(long frames)
    {
        while (frames > 0)
        {
            var chunkFrames = (int)Math.Min(frames, SilenceChunkFrames);
            _writer.Write(_silence.AsSpan(0, chunkFrames * _format.BlockAlign));
            _writtenFrames += chunkFrames;
            frames -= chunkFrames;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        Stop();

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        _recorder.DataAvailable -= OnDataAvailable;
        _recorder.Dispose();
        _writer.Dispose();
    }
}
