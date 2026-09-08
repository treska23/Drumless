using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

/// <summary>
/// Graba el flujo que Windows está renderizando realmente por un endpoint WASAPI.
/// No inspecciona ni modifica procesos concretos (WebView2 incluido).
/// </summary>
internal sealed class OutputEndpointLoopbackRecorder : IDisposable
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int SilenceChunkFrames = 4_096;

    private readonly object _gate = new();
    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly WasapiRecorder _capture;
    private readonly WaveFileWriter _writer;
    private readonly WaveFormat _format;
    private readonly byte[] _silence;
    private long _timelineStartTimestamp;
    private long _writtenFrames;
    private int _stopStarted;
    private bool _started;
    private bool _stopped;
    private bool _disposed;
    private bool _receivedPayload;
    private Exception? _failure;

    private OutputEndpointLoopbackRecorder(
        MMDeviceEnumerator enumerator,
        MMDevice device,
        WasapiRecorder capture,
        WaveFileWriter writer,
        string path)
    {
        _enumerator = enumerator;
        _device = device;
        _capture = capture;
        _writer = writer;
        _format = capture.WaveFormat;
        Path = path;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;
        _silence = new byte[SilenceChunkFrames * Math.Max(1, _format.BlockAlign)];
        _capture.DataAvailable += OnDataAvailable;
    }

    public string Path { get; }
    public string DeviceId { get; }
    public string DeviceName { get; }
    public WaveFormat WaveFormat => _format;
    public bool HasCapturedPayload
    {
        get
        {
            lock (_gate)
            {
                return _receivedPayload;
            }
        }
    }

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

    public static OutputEndpointLoopbackRecorder Prepare(string deviceId, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var resolvedPath = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(resolvedPath)!);

        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        WasapiRecorder? capture = null;
        WaveFileWriter? writer = null;
        try
        {
            enumerator = new MMDeviceEnumerator();
            device = enumerator.GetDevice(deviceId);
            var requestedFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
            capture = new WasapiRecorderBuilder()
                .WithDevice(device)
                .WithLoopbackCapture()
                .WithSharedMode()
                .WithEventSync()
                .WithFormat(requestedFormat)
                .WithBufferLength(40)
                .WithMmcssThreadPriority("Audio")
                .Build();
            writer = new WaveFileWriter(resolvedPath, capture.WaveFormat);
            return new OutputEndpointLoopbackRecorder(
                enumerator,
                device,
                capture,
                writer,
                resolvedPath);
        }
        catch
        {
            writer?.Dispose();
            capture?.Dispose();
            device?.Dispose();
            enumerator?.Dispose();
            throw;
        }
    }

    public void Start(long timelineStartTimestamp)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started || _stopped)
            {
                throw new InvalidOperationException("La captura del endpoint ya se inició o terminó.");
            }

            _timelineStartTimestamp = timelineStartTimestamp > 0
                ? timelineStartTimestamp
                : Stopwatch.GetTimestamp();
            _writtenFrames = 0;
            _started = true;
        }

        try
        {
            _capture.StartRecording();
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
                var packetFrames = buffer.Length / Math.Max(1, _format.BlockAlign);
                var elapsedFrames = GetElapsedFrames();
                var packetStartFrame = Math.Max(0L, elapsedFrames - packetFrames);
                if (packetStartFrame > _writtenFrames)
                {
                    WriteSilence(packetStartFrame - _writtenFrames);
                }

                if (buffer.IsEmpty || flags.HasFlag(AudioClientBufferFlags.Silent))
                {
                    return;
                }

                var overlapFrames = Math.Max(0L, _writtenFrames - packetStartFrame);
                if (overlapFrames >= packetFrames)
                {
                    return;
                }

                var skipBytes = checked((int)overlapFrames * _format.BlockAlign);
                var payload = buffer[skipBytes..];
                if (payload.IsEmpty)
                {
                    return;
                }

                _writer.Write(payload);
                _writtenFrames += payload.Length / _format.BlockAlign;
                _receivedPayload = true;
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
                _capture.StopRecording();
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
        if (_timelineStartTimestamp == 0)
        {
            return 0;
        }

        var elapsed = Stopwatch.GetElapsedTime(_timelineStartTimestamp);
        return Math.Max(
            0L,
            (long)Math.Round(elapsed.TotalSeconds * Math.Max(1, _format.SampleRate)));
    }

    private void WriteSilence(long frames)
    {
        while (frames > 0)
        {
            var chunkFrames = (int)Math.Min(frames, SilenceChunkFrames);
            var bytes = chunkFrames * _format.BlockAlign;
            _writer.Write(_silence, 0, bytes);
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

        _capture.DataAvailable -= OnDataAvailable;
        _capture.Dispose();
        _writer.Dispose();
        _device.Dispose();
        _enumerator.Dispose();
    }
}
