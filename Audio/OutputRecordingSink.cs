using System.Collections.Concurrent;
using System.Buffers;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class OutputRecordingSink : IDisposable
{
    private readonly ConcurrentQueue<(float[] Buffer, int Length)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly object _gate = new();
    private Task? _writerTask;
    private volatile bool _accepting;
    private bool _disposed;

    public bool IsRecording => _accepting;
    public string? CurrentPath { get; private set; }

    public void Start(string path, WaveFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(format);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_writerTask is not null)
            {
                throw new InvalidOperationException("Ya hay una grabación en curso.");
            }

            var resolved = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(resolved) ??
                            throw new InvalidOperationException("La ruta de grabación no tiene carpeta.");
            Directory.CreateDirectory(directory);
            CurrentPath = resolved;
            _accepting = true;
            _writerTask = Task.Run(() => WriteLoop(resolved, format));
        }
    }

    public void Capture(ReadOnlySpan<float> samples)
    {
        if (!_accepting || samples.IsEmpty)
        {
            return;
        }

        var buffer = ArrayPool<float>.Shared.Rent(samples.Length);
        samples.CopyTo(buffer);
        if (!_accepting)
        {
            ArrayPool<float>.Shared.Return(buffer);
            return;
        }

        _queue.Enqueue((buffer, samples.Length));
        _signal.Release();
    }

    public async Task<string?> StopAsync()
    {
        Task? writer;
        string? path;
        lock (_gate)
        {
            _accepting = false;
            writer = _writerTask;
            path = CurrentPath;
            if (writer is null)
            {
                return null;
            }
            _signal.Release();
        }

        try
        {
            await writer.ConfigureAwait(false);
            return path;
        }
        finally
        {
            lock (_gate)
            {
                _writerTask = null;
                CurrentPath = null;
            }
        }
    }

    private void WriteLoop(string path, WaveFormat format)
    {
        try
        {
            using var writer = new WaveFileWriter(path, format);
            while (_accepting || !_queue.IsEmpty)
            {
                _signal.Wait(250);
                while (_queue.TryDequeue(out var item))
                {
                    try
                    {
                        writer.WriteSamples(item.Buffer, 0, item.Length);
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(item.Buffer);
                    }
                }
            }
        }
        catch
        {
            _accepting = false;
            while (_queue.TryDequeue(out var item))
            {
                ArrayPool<float>.Shared.Return(item.Buffer);
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        StopAsync().GetAwaiter().GetResult();
        _signal.Dispose();
    }
}
