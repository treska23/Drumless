namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    private bool _youtubeTransportAttached;
    private bool _youtubeTransportSeeking;
    private bool _youtubeTransportPlaying;
    private double _youtubeTransportDurationSeconds = 1d;

    public event EventHandler<double>? YouTubeSeekRequested;

    public void AttachYouTubeTransport()
    {
        if (_youtubeTransportAttached)
        {
            return;
        }

        _youtubeTransportAttached = true;
        _transportTimer.Tick += OnYouTubeTransportTick;
    }

    public void BeginYouTubeTransport()
    {
        _youtubePerformancePositionSeconds = 0d;
        _youtubeTransportDurationSeconds = 1d;
        _youtubeTransportPlaying = false;
        _youtubeTransportSeeking = false;
        ApplyYouTubeTransportToSharedControls();
    }

    public void UpdateYouTubeTransport(
        double seconds,
        double durationSeconds,
        bool playing)
    {
        if (_currentYouTubeItem is null)
        {
            return;
        }

        if (double.IsFinite(durationSeconds) && durationSeconds > 0d)
        {
            _youtubeTransportDurationSeconds = Math.Max(1d, durationSeconds);
        }

        if (double.IsFinite(seconds) && seconds >= 0d)
        {
            _youtubePerformancePositionSeconds = Math.Clamp(
                seconds,
                0d,
                _youtubeTransportDurationSeconds);
            UpdateChordSheetPlaybackPosition(_youtubePerformancePositionSeconds);
        }

        _youtubeTransportPlaying = playing;
        if (!_youtubeTransportSeeking)
        {
            ApplyYouTubeTransportToSharedControls();
        }
    }

    public void SetYouTubeTransportPlaying(bool playing)
    {
        if (_currentYouTubeItem is null)
        {
            return;
        }

        _youtubeTransportPlaying = playing;
        if (!_youtubeTransportSeeking)
        {
            ApplyYouTubeTransportToSharedControls();
        }
    }

    public void BeginYouTubeTransportSeek()
    {
        if (_currentYouTubeItem is not null)
        {
            _youtubeTransportSeeking = true;
        }
    }

    public void CommitYouTubeTransportSeek(double seconds)
    {
        if (_currentYouTubeItem is null)
        {
            _youtubeTransportSeeking = false;
            return;
        }

        var target = Math.Clamp(
            double.IsFinite(seconds) ? seconds : _youtubePerformancePositionSeconds,
            0d,
            _youtubeTransportDurationSeconds);
        _youtubePerformancePositionSeconds = target;
        _youtubeTransportSeeking = false;
        ApplyYouTubeTransportToSharedControls();
        YouTubeSeekRequested?.Invoke(this, target);
    }

    private void OnYouTubeTransportTick(object? sender, EventArgs eventArgs)
    {
        if (_currentYouTubeItem is null || _youtubeTransportSeeking)
        {
            return;
        }

        // RefreshTransport actualiza primero los datos de la pista local. Este manejador se registró
        // después y sustituye esos valores por el estado real de YouTube cuando el elemento activo
        // pertenece a la cola integrada.
        ApplyYouTubeTransportToSharedControls();
    }

    private void ApplyYouTubeTransportToSharedControls()
    {
        var duration = Math.Max(1d, _youtubeTransportDurationSeconds);
        var position = Math.Clamp(_youtubePerformancePositionSeconds, 0d, duration);
        TrackDurationSeconds = duration;
        SetProperty(ref _trackProgress, position, nameof(TrackProgress));
        TrackPositionLabel = FormatTime(TimeSpan.FromSeconds(position));
        TrackDurationLabel = FormatTime(TimeSpan.FromSeconds(duration));
        PlayButtonLabel = _youtubeTransportPlaying ? "Pausar" : "Reproducir";
    }
}
