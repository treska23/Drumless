using System.Collections.Concurrent;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DrumPracticeStudio.Audio;

internal sealed class TrackTransportProvider : ISampleProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly ConcurrentQueue<TrackEndedNotification> _endedNotifications = new();
    private TrackSession? _session;
    private TrackPlaybackState _playbackState = TrackPlaybackState.NoTrack;
    private long _nextLoadGeneration;
    private long _latestRequestedLoadGeneration;
    private long _activeLoadGeneration;
    private long _nextRunGeneration;
    private long _activeRunGeneration;
    private double _durationSeconds;
    private double _positionSeconds;
    private float _volume = 0.8f;
    private bool _disposed;

    public TrackTransportProvider(WaveFormat waveFormat) => WaveFormat = waveFormat;

    public WaveFormat WaveFormat { get; }

    public bool IsPlaying
    {
        get
        {
            lock (_gate)
            {
                return _playbackState == TrackPlaybackState.Playing;
            }
        }
    }

    public TrackPlaybackState PlaybackState
    {
        get
        {
            lock (_gate)
            {
                return _playbackState;
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromSeconds(_durationSeconds);
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                return TimeSpan.FromSeconds(_positionSeconds);
            }
        }
    }

    public async Task<long> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        long loadGeneration;
        lock (_gate)
        {
            ThrowIfDisposed();
            loadGeneration = ++_nextLoadGeneration;
            _latestRequestedLoadGeneration = loadGeneration;
            InvalidateActiveRun(clearNotifications: true);
            if (_playbackState == TrackPlaybackState.Playing)
            {
                _playbackState = TrackPlaybackState.Paused;
            }
        }

        TrackSession? preparedSession = null;
        TrackSession? previousSession = null;
        try
        {
            // AudioFileReader can take noticeable time to open compressed files. It is
            // prepared away from both the UI and render threads, then installed under
            // the short transport lock below.
            preparedSession = await Task.Run(
                () => TrackSession.Create(path, WaveFormat.SampleRate),
                CancellationToken.None).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            lock (_gate)
            {
                ThrowIfDisposed();
                if (loadGeneration != _latestRequestedLoadGeneration)
                {
                    throw new OperationCanceledException(
                        $"La carga de pista {loadGeneration} fue reemplazada por una solicitud posterior.");
                }

                previousSession = _session;
                _session = preparedSession;
                preparedSession = null;
                _activeLoadGeneration = loadGeneration;
                _activeRunGeneration = 0;
                _durationSeconds = _session.Reader.TotalTime.TotalSeconds;
                _positionSeconds = 0d;
                _playbackState = TrackPlaybackState.Stopped;
            }

            previousSession?.Dispose();
            return loadGeneration;
        }
        finally
        {
            // Covers cancellation, supersession, disposal during preparation and
            // failures while installing a newly opened reader.
            preparedSession?.Dispose();
        }
    }

    public long Play()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_session is null)
            {
                return 0;
            }

            ClearEndedNotifications();
            if (_playbackState == TrackPlaybackState.Ended ||
                _session.Reader.Position >= _session.Reader.Length)
            {
                _session.Seek(TimeSpan.Zero);
                _positionSeconds = 0d;
            }

            _activeRunGeneration = ++_nextRunGeneration;
            _playbackState = TrackPlaybackState.Playing;
            return _activeRunGeneration;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            InvalidateActiveRun(clearNotifications: true);
            if (_playbackState == TrackPlaybackState.Playing)
            {
                _playbackState = TrackPlaybackState.Paused;
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            InvalidateActiveRun(clearNotifications: true);
            if (_session is null)
            {
                _playbackState = TrackPlaybackState.NoTrack;
                _positionSeconds = 0d;
                return;
            }

            _session.Seek(TimeSpan.Zero);
            _positionSeconds = 0d;
            _playbackState = TrackPlaybackState.Stopped;
        }
    }

    public void Unload()
    {
        TrackSession? session;
        lock (_gate)
        {
            ThrowIfDisposed();
            _latestRequestedLoadGeneration = ++_nextLoadGeneration;
            _activeLoadGeneration = 0;
            InvalidateActiveRun(clearNotifications: true);
            _durationSeconds = 0d;
            _positionSeconds = 0d;
            _playbackState = TrackPlaybackState.NoTrack;
            session = _session;
            _session = null;
        }

        session?.Dispose();
    }

    public long Seek(TimeSpan position)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var wasPlaying = _playbackState == TrackPlaybackState.Playing;
            InvalidateActiveRun(clearNotifications: true);
            if (_session is null)
            {
                return 0;
            }

            _session.Seek(position);
            _positionSeconds = _session.Reader.CurrentTime.TotalSeconds;

            if (wasPlaying)
            {
                // Seeking invalidates a completion already produced by the former
                // timeline, but playback itself continues as a new run.
                _activeRunGeneration = ++_nextRunGeneration;
                _playbackState = TrackPlaybackState.Playing;
                return _activeRunGeneration;
            }

            if (_playbackState == TrackPlaybackState.Ended &&
                _session.Reader.Position < _session.Reader.Length)
            {
                _playbackState = TrackPlaybackState.Paused;
            }

            return 0;
        }
    }

    public void SetVolume(float volume)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _volume = Math.Clamp(volume, 0f, 1f);
        }
    }

    public bool TryDequeueTrackEnded(out TrackEndedNotification notification) =>
        _endedNotifications.TryDequeue(out notification);

    public int Read(Span<float> buffer)
    {
        buffer.Clear();
        lock (_gate)
        {
            if (_disposed || _playbackState != TrackPlaybackState.Playing || _session is null)
            {
                return buffer.Length;
            }

            var read = _session.Provider.Read(buffer);
            for (var index = 0; index < read; index++)
            {
                buffer[index] *= _volume;
            }

            _positionSeconds = _session.Reader.CurrentTime.TotalSeconds;
            if (read < buffer.Length)
            {
                buffer[read..].Clear();
                _positionSeconds = _durationSeconds;
                _playbackState = TrackPlaybackState.Ended;

                // State changes to Ended before publishing, therefore subsequent
                // render calls cannot publish the same natural completion twice.
                if (_activeLoadGeneration != 0 && _activeRunGeneration != 0)
                {
                    _endedNotifications.Enqueue(new TrackEndedNotification(
                        _activeLoadGeneration,
                        _activeRunGeneration));
                }
            }

            return buffer.Length;
        }
    }

    public void Dispose()
    {
        TrackSession? session;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _latestRequestedLoadGeneration = ++_nextLoadGeneration;
            _activeLoadGeneration = 0;
            _activeRunGeneration = 0;
            _durationSeconds = 0d;
            _positionSeconds = 0d;
            _playbackState = TrackPlaybackState.Disposed;
            ClearEndedNotifications();
            session = _session;
            _session = null;
        }

        session?.Dispose();
    }

    private void InvalidateActiveRun(bool clearNotifications)
    {
        _activeRunGeneration = 0;
        if (clearNotifications)
        {
            ClearEndedNotifications();
        }
    }

    private void ClearEndedNotifications()
    {
        while (_endedNotifications.TryDequeue(out _))
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class TrackSession : IDisposable
    {
        private TrackSession(AudioFileReader reader, int targetSampleRate)
        {
            Reader = reader;
            TargetSampleRate = targetSampleRate;
            Provider = BuildProvider();
        }

        public AudioFileReader Reader { get; }
        public int TargetSampleRate { get; }
        public ISampleProvider Provider { get; private set; }

        public static TrackSession Create(string path, int targetSampleRate)
        {
            var reader = new AudioFileReader(path);
            try
            {
                return new TrackSession(reader, targetSampleRate);
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        public void Seek(TimeSpan position)
        {
            var bounded = position < TimeSpan.Zero
                ? TimeSpan.Zero
                : position > Reader.TotalTime ? Reader.TotalTime : position;
            Reader.CurrentTime = bounded;
            Provider = BuildProvider();
        }

        public void Dispose() => Reader.Dispose();

        private ISampleProvider BuildProvider()
        {
            ISampleProvider provider = Reader;
            provider = provider.WaveFormat.Channels switch
            {
                1 => new MonoToStereoSampleProvider(provider),
                2 => provider,
                _ => throw new NotSupportedException("La pista local debe ser mono o estéreo.")
            };

            return provider.WaveFormat.SampleRate == TargetSampleRate
                ? provider
                : new WdlResamplingSampleProvider(provider, TargetSampleRate);
        }
    }
}
