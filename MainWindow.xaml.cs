using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Data;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using DrumPracticeStudio.ViewModels;
using DrumPracticeStudio.Views;
using Microsoft.Web.WebView2.Core;

namespace DrumPracticeStudio;

public partial class MainWindow : Window
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int WmDpiChanged = 0x02E0;
    private const int WmDisplayChange = 0x007E;
    private const int WmExitSizeMove = 0x0232;
    private const double WorkAreaMarginDip = 12;

    private readonly MainViewModel _viewModel;
    private HwndSource? _windowSource;
    private bool _isFittingWindow;
    private bool _isYouTubeInitializing;
    private long _youtubePlaybackRequestVersion;
    private long _youtubeAudioProbeVersion;
    private int _youtubeAudioRoutingInProgress;
    private YouTubePlaybackRequest? _pendingYouTubePlayback;
    private PlaylistWindow? _playlistWindow;
    private ChordSheetWindow? _chordSheetWindow;
    private readonly HashSet<ComboBox> _configuredVstEffectPickers = [];

    private void OnMixerFaderPreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs eventArgs)
    {
        if (sender is not Slider slider ||
            slider.Template.FindName("PART_Track", slider) is not Track track ||
            track.ActualHeight <= 0)
        {
            return;
        }

        if (track.Thumb?.IsMouseOver == true)
        {
            // El Thumb recibe el gesto completo para que WPF pueda capturarlo y arrastrarlo.
            return;
        }

        var thumbHeight = track.Thumb?.ActualHeight ?? 0d;
        var point = eventArgs.GetPosition(track);
        slider.Value = MixerFaderMath.ValueFromVerticalPoint(
            slider.Minimum,
            slider.Maximum,
            track.ActualHeight,
            thumbHeight,
            point.Y);
        slider.Focus();
        eventArgs.Handled = true;
    }
    private Point? _libraryDragOrigin;
    private Point? _playlistDragOrigin;
    private Point? _effectSlotDragOrigin;
    private EffectSlotDragData? _effectSlotDragCandidate;
    private bool _closeAfterRecording;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        _viewModel.YouTubePlaybackRequested += OnYouTubePlaybackRequested;
        _viewModel.YouTubeControlRequested += OnYouTubeControlRequested;
        _viewModel.YouTubeMetronomeChanged += OnYouTubeMetronomeChanged;
        _viewModel.ChordSheetWindowRequested += OnChordSheetWindowRequested;
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);
        FitToCurrentMonitor(center: true);
    }

    private IntPtr WindowMessageHook(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message is WmDpiChanged or WmDisplayChange or WmExitSizeMove)
        {
            Dispatcher.BeginInvoke(() => FitToCurrentMonitor(center: false));
        }

        return IntPtr.Zero;
    }

    private void FitToCurrentMonitor(bool center)
    {
        if (_isFittingWindow || WindowState != WindowState.Normal)
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var windowRect))
        {
            return;
        }

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return;
        }

        _isFittingWindow = true;
        try
        {
            var dpi = GetDpiForWindow(handle);
            var dpiScale = dpi > 0 ? dpi / 96d : 1d;
            var margin = Math.Max(1, (int)Math.Round(WorkAreaMarginDip * dpiScale));
            var workWidth = monitorInfo.Work.Right - monitorInfo.Work.Left;
            var workHeight = monitorInfo.Work.Bottom - monitorInfo.Work.Top;
            var availableWidth = Math.Max(1, workWidth - (margin * 2));
            var availableHeight = Math.Max(1, workHeight - (margin * 2));

            MaxWidth = availableWidth / dpiScale;
            MaxHeight = availableHeight / dpiScale;

            var width = Math.Min(windowRect.Right - windowRect.Left, availableWidth);
            var height = Math.Min(windowRect.Bottom - windowRect.Top, availableHeight);
            var left = center
                ? monitorInfo.Work.Left + ((workWidth - width) / 2)
                : Math.Clamp(
                    windowRect.Left,
                    monitorInfo.Work.Left + margin,
                    monitorInfo.Work.Right - margin - width);
            var top = center
                ? monitorInfo.Work.Top + ((workHeight - height) / 2)
                : Math.Clamp(
                    windowRect.Top,
                    monitorInfo.Work.Top + margin,
                    monitorInfo.Work.Bottom - margin - height);

            SetWindowPos(
                handle,
                IntPtr.Zero,
                left,
                top,
                width,
                height,
                SwpNoZOrder | SwpNoActivate);
        }
        finally
        {
            _isFittingWindow = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _windowSource?.RemoveHook(WindowMessageHook);
        _viewModel.YouTubePlaybackRequested -= OnYouTubePlaybackRequested;
        _viewModel.YouTubeControlRequested -= OnYouTubeControlRequested;
        _viewModel.YouTubeMetronomeChanged -= OnYouTubeMetronomeChanged;
        _viewModel.ChordSheetWindowRequested -= OnChordSheetWindowRequested;
        YouTubeWebView.Dispose();
        _playlistWindow?.Close();
        _chordSheetWindow?.Close();
        _viewModel.Dispose();
    }

    private void OnChordSheetWindowRequested(object? sender, EventArgs e)
    {
        if (_chordSheetWindow is { IsVisible: true })
        {
            _chordSheetWindow.Activate();
            return;
        }
        _chordSheetWindow = new ChordSheetWindow(_viewModel) { Owner = this };
        _chordSheetWindow.Closed += (_, _) => _chordSheetWindow = null;
        _chordSheetWindow.Show();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAfterRecording || !_viewModel.IsRecordingOutput)
        {
            return;
        }

        e.Cancel = true;
        IsEnabled = false;
        try
        {
            await _viewModel.CompleteRecordingBeforeCloseAsync();
        }
        finally
        {
            _closeAfterRecording = true;
            Close();
        }
    }

    private async void OnYouTubeWebViewLoaded(object sender, RoutedEventArgs e) =>
        await EnsureYouTubeReadyAsync();

    private async Task EnsureYouTubeReadyAsync()
    {
        if (YouTubeWebView.CoreWebView2 is not null || _isYouTubeInitializing)
        {
            return;
        }

        _isYouTubeInitializing = true;
        try
        {
            YouTubeStatusText.Text = "Iniciando reproductor oficial…";
            Directory.CreateDirectory(AppPaths.YouTubeWebViewData);
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppPaths.YouTubeWebViewData);
            await YouTubeWebView.EnsureCoreWebView2Async(environment);
            var core = YouTubeWebView.CoreWebView2 ??
                throw new InvalidOperationException("WebView2 no devolvió un navegador inicializado.");

            if (core.Profile.IsInPrivateModeEnabled)
            {
                throw new InvalidOperationException(
                    "El perfil de YouTube se inició en modo privado y no puede conservar la sesión.");
            }
            core.Profile.PreferredTrackingPreventionLevel =
                CoreWebView2TrackingPreventionLevel.Basic;

            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.NewWindowRequested += OnYouTubeNewWindowRequested;
            core.ProcessFailed += OnYouTubeProcessFailed;
            core.WebMessageReceived += OnYouTubeWebMessageReceived;
            if (YouTubeWebView.Source is null)
            {
                YouTubeWebView.Source = YouTubeNavigationService.HomeUri;
            }
            YouTubeStatusText.Text = "YouTube preparado · sesión persistente";
        }
        catch (Exception exception)
        {
            YouTubeStatusText.Text = $"No se pudo iniciar YouTube: {exception.Message}";
        }
        finally
        {
            _isYouTubeInitializing = false;
        }
    }

    private async void OnYouTubeSearchClick(object sender, RoutedEventArgs e) =>
        await SearchYouTubeAsync();

    private async void OnYouTubeSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await SearchYouTubeAsync();
    }

    private async Task SearchYouTubeAsync()
    {
        var query = YouTubeSearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            YouTubeStatusText.Text = "Escribe algo para buscar";
            YouTubeSearchBox.Focus();
            return;
        }

        await EnsureYouTubeReadyAsync();
        if (YouTubeWebView.CoreWebView2 is null)
        {
            return;
        }

        if (YouTubeNavigationService.TryGetNavigationUri(query, out var directUri))
        {
            YouTubeStatusText.Text = YouTubeNavigationService.TryGetPlaylistId(directUri, out _)
                ? "Abriendo playlist de YouTube…"
                : "Abriendo enlace de YouTube…";
            YouTubeWebView.Source = directUri;
            return;
        }

        YouTubeStatusText.Text = $"Buscando «{query}»…";
        YouTubeWebView.Source = YouTubeNavigationService.CreateSearchUri(query);
    }

    private void OnYouTubeBackClick(object sender, RoutedEventArgs e)
    {
        if (YouTubeWebView.CanGoBack)
        {
            YouTubeWebView.GoBack();
        }
    }

    private void OnYouTubeForwardClick(object sender, RoutedEventArgs e)
    {
        if (YouTubeWebView.CanGoForward)
        {
            YouTubeWebView.GoForward();
        }
    }

    private async void OnYouTubeHomeClick(object sender, RoutedEventArgs e)
    {
        await EnsureYouTubeReadyAsync();
        YouTubeWebView.Source = YouTubeNavigationService.HomeUri;
    }

    private async void OnYouTubeReloadClick(object sender, RoutedEventArgs e)
    {
        await EnsureYouTubeReadyAsync();
        YouTubeWebView.Reload();
    }

    private void OnYouTubeInitializationCompleted(
        object sender,
        CoreWebView2InitializationCompletedEventArgs e)
    {
        YouTubeStatusText.Text = e.IsSuccess
            ? "YouTube preparado"
            : $"No se pudo iniciar YouTube: {e.InitializationException?.Message}";
    }

    private void OnYouTubeNavigationStarting(
        object sender,
        CoreWebView2NavigationStartingEventArgs e) =>
        YouTubeStatusText.Text = "Cargando YouTube…";

    private async void OnYouTubeNavigationCompleted(
        object sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        YouTubeStatusText.Text = e.IsSuccess
            ? "Listo · elige un vídeo para reproducirlo"
            : $"YouTube no pudo cargar la página ({e.WebErrorStatus})";
        if (e.IsSuccess && YouTubeWebView.CoreWebView2 is not null)
        {
            await YouTubeWebView.CoreWebView2.ExecuteScriptAsync(
                """
                (() => {
                  if (window.__drumPracticeEndObserver) return;
                  window.__drumPracticeEndObserver = true;
                  const attach = () => {
                    const video = document.querySelector('video');
                    if (!video || video.__drumPracticeAttached) return;
                    video.__drumPracticeAttached = true;
                    const notifyState = () => chrome.webview.postMessage({
                      type: 'video-state',
                      playing: !video.paused && !video.ended,
                      videoId: new URL(location.href).searchParams.get('v') || ''
                    });
                    video.addEventListener('play', notifyState);
                    video.addEventListener('pause', notifyState);
                    video.addEventListener('pause', () => window.__dpsMetronomeReset?.());
                    video.addEventListener('seeking', () => window.__dpsMetronomeReset?.());
                    video.addEventListener('ended', () => {
                      notifyState();
                      const id = new URL(location.href).searchParams.get('v') || '';
                      chrome.webview.postMessage({ type: 'video-ended', videoId: id });
                    });
                  };
                  attach();
                  new MutationObserver(attach).observe(document.documentElement, { childList: true, subtree: true });

                  if (!window.__dpsMetronomeInstalled) {
                    window.__dpsMetronomeInstalled = true;
                    let config = null;
                    let context = null;
                    let nextBeat = null;
                    let currentSegmentId = null;
                    let lastPositionNotice = 0;
                    const scheduled = new Set();
                    const reset = () => {
                      nextBeat = null;
                      currentSegmentId = null;
                      for (const oscillator of scheduled) {
                        try { oscillator.stop(); } catch (_) {}
                      }
                      scheduled.clear();
                    };
                    window.__dpsMetronomeReset = reset;
                    window.__dpsSetMetronome = value => {
                      config = value;
                      reset();
                    };
                    setInterval(() => {
                      const video = document.querySelector('video');
                      if (!video || video.paused || video.ended) return;
                      const nowTicks = performance.now();
                      if (nowTicks - lastPositionNotice >= 80) {
                        lastPositionNotice = nowTicks;
                        chrome.webview.postMessage({
                          type: 'video-position',
                          seconds: video.currentTime
                        });
                      }
                      if (!config?.metronomeEnabled) return;
                      context ??= new AudioContext({ latencyHint: 'interactive' });
                      if (context.state === 'suspended') context.resume().catch(() => {});
                      const nowInVideo = video.currentTime;
                      const segments = Array.isArray(config.segments) && config.segments.length
                        ? config.segments
                        : [{
                            id: 'base',
                            startSeconds: 0,
                            bpm: config.bpm,
                            firstBeatSeconds: config.firstBeatSeconds,
                            beatsPerBar: config.beatsPerBar
                          }];
                      let segmentIndex = 0;
                      for (let index = 1; index < segments.length; index++) {
                        if ((segments[index].startSeconds || 0) > nowInVideo) break;
                        segmentIndex = index;
                      }
                      const segment = segments[segmentIndex];
                      const segmentId = segment.id || String(segmentIndex);
                      const beatSeconds = 60 / segment.bpm;
                      const firstBeat = Math.max(0, segment.firstBeatSeconds || 0);
                      const segmentEnd = segmentIndex + 1 < segments.length
                        ? segments[segmentIndex + 1].startSeconds
                        : Number.POSITIVE_INFINITY;
                      if (currentSegmentId !== segmentId) {
                        currentSegmentId = segmentId;
                        nextBeat = null;
                      }
                      if (nextBeat === null || nextBeat < nowInVideo - 0.05) {
                        const beatNumber = Math.max(0, Math.ceil((nowInVideo - firstBeat) / beatSeconds - 1e-7));
                        nextBeat = firstBeat + beatNumber * beatSeconds;
                      }
                      while (nextBeat <= nowInVideo + 0.10 && nextBeat < segmentEnd) {
                        const beatNumber = Math.max(0, Math.round((nextBeat - firstBeat) / beatSeconds));
                        const accent = beatNumber % Math.max(1, segment.beatsPerBar || 4) === 0;
                        const oscillator = context.createOscillator();
                        const gain = context.createGain();
                        const start = context.currentTime + Math.max(0, nextBeat - nowInVideo);
                        oscillator.frequency.value = accent ? 1760 : 1180;
                        gain.gain.setValueAtTime((config.metronomeVolume || 0.55) * (accent ? 0.8 : 0.55), start);
                        gain.gain.exponentialRampToValueAtTime(0.0001, start + 0.028);
                        oscillator.connect(gain).connect(context.destination);
                        oscillator.start(start);
                        oscillator.stop(start + 0.03);
                        chrome.webview.postMessage({ type: 'metronome-click', videoTime: nextBeat });
                        scheduled.add(oscillator);
                        oscillator.onended = () => scheduled.delete(oscillator);
                        nextBeat += beatSeconds;
                      }
                    }, 25);
                  }
                })();
                """);
            var youtubeTempo = _viewModel.CurrentYouTubeTempoSettings;
            await ApplyYouTubeMetronomeAsync(youtubeTempo);
            if (youtubeTempo is { MetronomeEnabled: true })
            {
                YouTubeStatusText.Text =
                    $"Claqueta YouTube preparada · {youtubeTempo.Bpm:0.##} BPM · primer pulso " +
                    $"{youtubeTempo.FirstBeatSeconds:0.000} s";
            }

            await StartPendingYouTubePlaybackAsync();
        }
    }

    private void OnYouTubeNewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        YouTubeWebView.CoreWebView2.Navigate(e.Uri);
    }

    private void OnYouTubeProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Increment(ref _youtubeAudioProbeVersion);
            _viewModel.StopYouTubeAudioRouting(
                "El audio de YouTube se desconectó del mezclador; vuelve a reproducir para reconectarlo.");
            YouTubeStatusText.Text = $"El reproductor de YouTube se detuvo ({e.ProcessFailedKind})";
        });

    private async void OnYouTubePlaybackRequested(object? sender, YouTubePlaybackRequest request)
    {
        var requestVersion = ++_youtubePlaybackRequestVersion;
        _pendingYouTubePlayback = request;
        await EnsureYouTubeReadyAsync();
        if (requestVersion != _youtubePlaybackRequestVersion ||
            YouTubeWebView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            await YouTubeWebView.CoreWebView2.ExecuteScriptAsync(
                "document.querySelector('video')?.pause();");
        }
        catch (InvalidOperationException)
        {
            // La navegación anterior puede destruir el documento mientras se pausa.
        }

        if (requestVersion == _youtubePlaybackRequestVersion)
        {
            YouTubeWebView.Source = request.Uri;
        }
    }

    private async Task StartPendingYouTubePlaybackAsync()
    {
        var pending = _pendingYouTubePlayback;
        var core = YouTubeWebView.CoreWebView2;
        if (pending is null ||
            core is null ||
            !YouTubeNavigationService.TryGetVideoId(YouTubeWebView.Source, out var currentVideoId) ||
            !string.Equals(currentVideoId, pending.VideoId, StringComparison.Ordinal))
        {
            return;
        }

        var expectedVideoId = JsonSerializer.Serialize(pending.VideoId);
        await core.ExecuteScriptAsync(
            $$"""
            (() => {
              clearInterval(window.__dpsAutoplayTimer);
              const expectedVideoId = {{expectedVideoId}};
              let attempts = 0;
              let lastError = '';
              window.__dpsAutoplayTimer = setInterval(async () => {
                const currentVideoId = new URL(location.href).searchParams.get('v') || '';
                if (currentVideoId !== expectedVideoId) {
                  clearInterval(window.__dpsAutoplayTimer);
                  return;
                }

                const video = document.querySelector('video');
                attempts += 1;
                if (video) {
                  video.muted = false;
                  try {
                    await video.play();
                    clearInterval(window.__dpsAutoplayTimer);
                    chrome.webview.postMessage({
                      type: 'video-state',
                      playing: true,
                      videoId: expectedVideoId
                    });
                    return;
                  } catch (error) {
                    lastError = String(error?.message || error || '');
                  }
                }

                if (attempts >= 80) {
                  clearInterval(window.__dpsAutoplayTimer);
                  chrome.webview.postMessage({
                    type: 'playback-error',
                    videoId: expectedVideoId,
                    message: lastError || 'YouTube no creó el reproductor a tiempo'
                  });
                }
              }, 125);
            })();
            """);
    }

    private async void OnYouTubeControlRequested(object? sender, YouTubeControlRequest request)
    {
        if (YouTubeWebView.CoreWebView2 is null)
        {
            return;
        }

        var script = request.Action switch
        {
            YouTubeControlAction.Toggle =>
                "(() => { const v=document.querySelector('video'); if(v) v.paused ? v.play() : v.pause(); })()",
            YouTubeControlAction.Pause =>
                "(() => { const v=document.querySelector('video'); if(v) v.pause(); })()",
            YouTubeControlAction.Stop =>
                "(() => { const v=document.querySelector('video'); if(v) { v.pause(); v.currentTime=0; } })()",
            _ => ""
        };
        if (script.Length > 0)
        {
            await YouTubeWebView.CoreWebView2.ExecuteScriptAsync(script);
        }
    }

    private async void OnYouTubeMetronomeChanged(
        object? sender,
        YouTubeMetronomeRequest request)
    {
        await ApplyYouTubeMetronomeAsync(request.Settings);
        if (request.Settings is { MetronomeEnabled: true } tempo)
        {
            YouTubeStatusText.Text =
                $"Claqueta YouTube preparada · {tempo.Bpm:0.##} BPM · primer pulso " +
                $"{tempo.FirstBeatSeconds:0.000} s";
        }
    }

    private async Task ApplyYouTubeMetronomeAsync(TempoSettings? settings)
    {
        if (YouTubeWebView.CoreWebView2 is null)
        {
            return;
        }

        var payload = settings is null
            ? "null"
            : JsonSerializer.Serialize(new
            {
                settings.Bpm,
                settings.FirstBeatSeconds,
                settings.BeatsPerBar,
                settings.MetronomeEnabled,
                settings.MetronomeVolume,
                Segments = settings.EffectiveSegments.Select(segment => new
                {
                    segment.Id,
                    segment.StartSeconds,
                    segment.Bpm,
                    segment.FirstBeatSeconds,
                    segment.BeatsPerBar
                })
            }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await YouTubeWebView.CoreWebView2.ExecuteScriptAsync(
            $"window.__dpsSetMetronome?.({payload});");
    }

    private async void OnYouTubeWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (root.GetProperty("type").GetString() == "video-ended" &&
                root.TryGetProperty("videoId", out var videoIdElement) &&
                videoIdElement.GetString() is { Length: > 0 } videoId)
            {
                await _viewModel.HandleYouTubeEndedAsync(videoId);
            }
            else if (root.GetProperty("type").GetString() == "video-state" &&
                     root.TryGetProperty("playing", out var playingElement) &&
                     playingElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var stateVideoIdText = root.TryGetProperty("videoId", out var stateVideoId)
                    ? stateVideoId.GetString()
                    : null;
                var playing = playingElement.GetBoolean();
                if (_viewModel.HandleYouTubePlaybackState(stateVideoIdText, playing) &&
                    playing)
                {
                    _pendingYouTubePlayback = null;
                    YouTubeStatusText.Text = "Reproduciendo desde la playlist";
                    _ = EnsureYouTubeAudioRoutingAsync();
                }
            }
            else if (root.GetProperty("type").GetString() == "playback-error" &&
                     root.TryGetProperty("videoId", out var failedVideoId))
            {
                var message = root.TryGetProperty("message", out var errorMessage)
                    ? errorMessage.GetString()
                    : null;
                _viewModel.HandleYouTubePlaybackFailure(
                    failedVideoId.GetString(),
                    message);
                YouTubeStatusText.Text =
                    "YouTube no pudo iniciar la reproducción automáticamente";
            }
            else if (root.GetProperty("type").GetString() == "video-position" &&
                     root.TryGetProperty("seconds", out var secondsElement) &&
                     secondsElement.TryGetDouble(out var playbackSeconds))
            {
                _viewModel.UpdateYouTubePlaybackPosition(playbackSeconds);
            }
            else if (root.GetProperty("type").GetString() == "metronome-click" &&
                     root.TryGetProperty("videoTime", out var clickTime) &&
                     clickTime.TryGetDouble(out var videoSeconds))
            {
                YouTubeStatusText.Text =
                    $"Claqueta sincronizada · pulso en {videoSeconds:0.000} s de vídeo";
            }
        }
        catch (JsonException)
        {
        }
    }

    private async Task EnsureYouTubeAudioRoutingAsync()
    {
        var core = YouTubeWebView.CoreWebView2;
        if (core is null ||
            _viewModel.IsYouTubeAudioRouted ||
            Interlocked.CompareExchange(ref _youtubeAudioRoutingInProgress, 1, 0) != 0)
        {
            return;
        }

        var probeVersion = Interlocked.Increment(ref _youtubeAudioProbeVersion);
        var routingStage = "preparar WebView2";
        try
        {
            core.IsMuted = false;
            routingStage = "obtener el proceso de WebView2";
            var browserProcessId = core.BrowserProcessId;
            routingStage = "crear captura por proceso";
            await _viewModel.StartYouTubeAudioRoutingAsync(browserProcessId);
            if (probeVersion != _youtubeAudioProbeVersion)
            {
                return;
            }

            _viewModel.TakeYouTubeAudioPeak();
            routingStage = "medir audio sin silenciar";
            await Task.Delay(1200);
            var unmutedPeak = _viewModel.TakeYouTubeAudioPeak();
            if (probeVersion != _youtubeAudioProbeVersion)
            {
                return;
            }

            if (unmutedPeak < 0.0005f)
            {
                _viewModel.StopYouTubeAudioRouting(
                    "No se detectó audio de YouTube para redirigir; se mantiene la salida normal del navegador.");
                return;
            }

            core.IsMuted = true;
            _viewModel.TakeYouTubeAudioPeak();
            routingStage = "medir audio con WebView2 silenciado";
            await Task.Delay(1200);
            var mutedPeak = _viewModel.TakeYouTubeAudioPeak();
            if (probeVersion != _youtubeAudioProbeVersion)
            {
                return;
            }

            if (mutedPeak >= 0.0005f)
            {
                _viewModel.ConfirmYouTubeAudioRouting();
                YouTubeStatusText.Text =
                    "Reproduciendo · audio enviado a la salida elegida";
                return;
            }

            core.IsMuted = false;
            _viewModel.StopYouTubeAudioRouting(
                "Este WebView2 no permite redirigir el audio sin silenciarlo; se mantiene la salida normal.");
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            COMException or
            NotSupportedException)
        {
            if (YouTubeWebView.CoreWebView2 is { } activeCore)
            {
                activeCore.IsMuted = false;
            }
            _viewModel.StopYouTubeAudioRouting(
                $"No se pudo conectar YouTube con la salida elegida al {routingStage}: " +
                exception.Message);
        }
        finally
        {
            Volatile.Write(ref _youtubeAudioRoutingInProgress, 0);
        }
    }

    private async void OnAddCurrentYouTubeToPlaylistClick(object sender, RoutedEventArgs e)
    {
        var uri = YouTubeWebView.Source;
        if (!YouTubeNavigationService.TryGetVideoId(uri, out var videoId))
        {
            YouTubeStatusText.Text = "Abre primero un vídeo concreto para añadirlo";
            return;
        }

        var title = "Vídeo de YouTube";
        if (YouTubeWebView.CoreWebView2 is not null)
        {
            var jsonTitle = await YouTubeWebView.CoreWebView2.ExecuteScriptAsync("document.title");
            title = JsonSerializer.Deserialize<string>(jsonTitle)?
                .Replace(" - YouTube", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Trim() ?? title;
        }

        if (_viewModel.AddYouTubeToSelectedPlaylist(
                videoId,
                YouTubeNavigationService.CreateWatchUri(videoId).AbsoluteUri,
                title,
                YouTubeNavigationService.CreateThumbnailUrl(videoId)))
        {
            YouTubeStatusText.Text = $"Añadido a la playlist: {title}";
        }
    }

    private async void OnImportCurrentYouTubePlaylistClick(object sender, RoutedEventArgs e)
    {
        if (!YouTubeNavigationService.TryGetPlaylistId(YouTubeWebView.Source, out _))
        {
            YouTubeStatusText.Text =
                "Abre una playlist de YouTube o pega su URL en el buscador";
            return;
        }
        if (YouTubeWebView.CoreWebView2 is null)
        {
            YouTubeStatusText.Text = "El navegador de YouTube todavía no está preparado";
            return;
        }

        var button = sender as Button;
        if (button is not null)
        {
            button.IsEnabled = false;
        }
        try
        {
            YouTubeStatusText.Text = "Leyendo la playlist completa de YouTube…";
            var scriptResult = await YouTubeWebView.CoreWebView2.ExecuteScriptAsync(
                YouTubePlaylistExtractionScript);
            var payloadJson = JsonSerializer.Deserialize<string>(scriptResult);
            var payload = string.IsNullOrWhiteSpace(payloadJson)
                ? null
                : JsonSerializer.Deserialize<YouTubePlaylistPayload>(
                    payloadJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (payload?.Items is not { Count: > 0 })
            {
                YouTubeStatusText.Text =
                    "No se encontraron vídeos. Espera a que YouTube termine de cargar la playlist y vuelve a intentarlo.";
                return;
            }

            var entries = payload.Items
                .Select(item => TryCreateYouTubePlaylistEntry(item, out var entry) ? entry : null)
                .Where(entry => entry is not null)
                .Cast<YouTubePlaylistEntry>()
                .DistinctBy(entry => entry.VideoId, StringComparer.Ordinal)
                .ToArray();
            if (entries.Length == 0)
            {
                YouTubeStatusText.Text = "YouTube no devolvió vídeos válidos para importar";
                return;
            }

            var result = _viewModel.ImportYouTubePlaylist(
                entries,
                string.IsNullOrWhiteSpace(payload.Title) ? "Playlist de YouTube" : payload.Title);
            YouTubeStatusText.Text = result.Added == 0
                ? $"0 añadidos · {result.Duplicates} ya estaban en la playlist activa"
                : $"{result.Added} vídeos añadidos · {result.Duplicates} duplicados omitidos";
        }
        catch (Exception exception)
        {
            YouTubeStatusText.Text = $"No se pudo importar la playlist: {exception.Message}";
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

    private static bool TryCreateYouTubePlaylistEntry(
        YouTubePlaylistItemPayload item,
        out YouTubePlaylistEntry entry)
    {
        entry = null!;
        if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ||
            !YouTubeNavigationService.TryGetVideoId(uri, out var videoId) ||
            !string.Equals(videoId, item.VideoId, StringComparison.Ordinal))
        {
            return false;
        }

        var title = string.IsNullOrWhiteSpace(item.Title)
            ? $"Vídeo {videoId}"
            : item.Title.Trim();
        entry = new YouTubePlaylistEntry(
            videoId,
            title,
            YouTubeNavigationService.CreateWatchUri(videoId).AbsoluteUri,
            string.IsNullOrWhiteSpace(item.ThumbnailUrl)
                ? YouTubeNavigationService.CreateThumbnailUrl(videoId)
                : item.ThumbnailUrl);
        return true;
    }

    private const string YouTubePlaylistExtractionScript =
        """
        (async () => {
          const playlistId = new URL(location.href).searchParams.get('list') || '';
          if (!playlistId) return JSON.stringify({ title: '', items: [] });

          const renderers = () => Array.from(document.querySelectorAll(
            'ytd-playlist-video-renderer, ytd-playlist-panel-video-renderer'));
          let previousCount = -1;
          let stablePasses = 0;
          for (let pass = 0; pass < 100 && stablePasses < 5; pass += 1) {
            const nodes = renderers();
            stablePasses = nodes.length === previousCount ? stablePasses + 1 : 0;
            previousCount = nodes.length;
            for (const selector of [
              'ytd-playlist-video-list-renderer #contents',
              'ytd-playlist-panel-renderer #items'
            ]) {
              const container = document.querySelector(selector);
              container?.lastElementChild?.scrollIntoView({ block: 'end' });
            }
            window.scrollTo(0, document.documentElement.scrollHeight);
            await new Promise(resolve => setTimeout(resolve, 350));
          }

          const seen = new Set();
          const items = [];
          for (const node of renderers()) {
            const link = node.querySelector('a#video-title, a#wc-endpoint, a[href*="watch?v="]');
            if (!link?.href) continue;
            const url = new URL(link.href, location.origin);
            const videoId = url.searchParams.get('v') || node.getAttribute('video-id') || '';
            if (!videoId || seen.has(videoId)) continue;
            seen.add(videoId);
            const titleNode = node.querySelector('#video-title');
            const title = (titleNode?.getAttribute('title') || titleNode?.textContent || '').trim();
            const image = node.querySelector('img');
            items.push({
              videoId,
              title,
              url: `https://www.youtube.com/watch?v=${encodeURIComponent(videoId)}`,
              thumbnailUrl: image?.currentSrc || image?.src || ''
            });
          }

          const title = (
            document.querySelector('ytd-playlist-header-renderer h1 yt-formatted-string')?.textContent ||
            document.querySelector('ytd-playlist-panel-renderer #title')?.textContent ||
            document.title.replace(/\s*-\s*YouTube\s*$/i, '')
          ).trim();
          return JSON.stringify({ title, items });
        })()
        """;

    private void OnLibraryDragStart(object sender, MouseButtonEventArgs e) =>
        _libraryDragOrigin = e.GetPosition(TrackLibraryList);

    private IReadOnlyList<LocalTrack> GetSelectedLibraryTracks() =>
        TrackLibraryList.SelectedItems.Cast<LocalTrack>().ToArray();

    private void OnLibrarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = GetSelectedLibraryTracks();
        _viewModel.SelectedLibraryTrack = selected.LastOrDefault();
        LibrarySelectionSummary.Text = selected.Count == 1
            ? "1 seleccionada"
            : $"{selected.Count} seleccionadas";
        LoadLibrarySelectionButton.IsEnabled =
            selected.Count == 1 && selected[0].IsAvailable;
        AddLibrarySelectionButton.IsEnabled = selected.Count > 0;
        RemoveLibrarySelectionButton.IsEnabled = selected.Count > 0;
    }

    private void OnLoadLibrarySelectionClick(object sender, RoutedEventArgs e) =>
        _viewModel.LoadLibrarySelection(GetSelectedLibraryTracks());

    private void OnAddLibrarySelectionToPlaylistClick(object sender, RoutedEventArgs e) =>
        _viewModel.AddLibrarySelectionToPlaylist(GetSelectedLibraryTracks());

    private void OnRemoveLibrarySelectionClick(object sender, RoutedEventArgs e)
    {
        _viewModel.RemoveLibrarySelection(GetSelectedLibraryTracks());
        UpdateLibrarySelectionControls();
    }

    private void UpdateLibrarySelectionControls() =>
        OnLibrarySelectionChanged(
            TrackLibraryList,
            new SelectionChangedEventArgs(
                Selector.SelectionChangedEvent,
                Array.Empty<object>(),
                Array.Empty<object>()));

    private void OnLibraryPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _libraryDragOrigin is not { } origin ||
            !ExceededDragThreshold(origin, e.GetPosition(TrackLibraryList)) ||
            TrackLibraryList.SelectedItem is not LocalTrack track)
        {
            return;
        }

        _libraryDragOrigin = null;
        DragDrop.DoDragDrop(TrackLibraryList, track, DragDropEffects.Copy);
    }

    private void OnPlaylistDragStart(object sender, MouseButtonEventArgs e) =>
        _playlistDragOrigin = e.GetPosition(PlaylistItemList);

    private IReadOnlyList<PlaylistItemViewModel> GetSelectedPlaylistItems() =>
        PlaylistItemList.SelectedItems.Cast<PlaylistItemViewModel>().ToArray();

    private void OnPlaylistSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = GetSelectedPlaylistItems();
        _viewModel.SelectedPlaylistItem = selected.LastOrDefault();
        PlaylistItemSelectionSummary.Text = selected.Count == 1
            ? "1 seleccionado"
            : $"{selected.Count} seleccionados";
        var single = selected.Count == 1;
        PlayPlaylistSelectionButton.IsEnabled = single && selected[0].IsAvailable;
        MovePlaylistSelectionUpButton.IsEnabled = single;
        MovePlaylistSelectionDownButton.IsEnabled = single;
        RemovePlaylistSelectionButton.IsEnabled = selected.Count > 0;
    }

    private void OnPlayPlaylistSelectionClick(object sender, RoutedEventArgs e) =>
        _viewModel.PlayPlaylistSelection(GetSelectedPlaylistItems());

    private void OnMovePlaylistSelectionUpClick(object sender, RoutedEventArgs e) =>
        _viewModel.MovePlaylistSelection(GetSelectedPlaylistItems(), moveUp: true);

    private void OnMovePlaylistSelectionDownClick(object sender, RoutedEventArgs e) =>
        _viewModel.MovePlaylistSelection(GetSelectedPlaylistItems(), moveUp: false);

    private void OnRemovePlaylistSelectionClick(object sender, RoutedEventArgs e) =>
        _viewModel.RemovePlaylistSelection(GetSelectedPlaylistItems());

    private void OnPlaylistPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _playlistDragOrigin is not { } origin ||
            !ExceededDragThreshold(origin, e.GetPosition(PlaylistItemList)) ||
            PlaylistItemList.SelectedItem is not PlaylistItemViewModel item)
        {
            return;
        }

        _playlistDragOrigin = null;
        DragDrop.DoDragDrop(PlaylistItemList, item, DragDropEffects.Move);
    }

    private void OnPlaylistDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(LocalTrack)) &&
            e.Data.GetData(typeof(LocalTrack)) is LocalTrack track)
        {
            _viewModel.AddTrackToSelectedPlaylist(track);
            return;
        }

        if (!e.Data.GetDataPresent(typeof(PlaylistItemViewModel)) ||
            e.Data.GetData(typeof(PlaylistItemViewModel)) is not PlaylistItemViewModel dragged)
        {
            return;
        }

        var target = FindItemFromSource<PlaylistItemViewModel>(PlaylistItemList, e.OriginalSource);
        var targetIndex = target is null
            ? _viewModel.PlaylistItems.Count - 1
            : _viewModel.PlaylistItems.IndexOf(target);
        _viewModel.MoveSelectedPlaylistItem(dragged, targetIndex);
    }

    private void OnPlaylistItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistItemList.SelectedItem is PlaylistItemViewModel item)
        {
            _viewModel.PlayPlaylistItem(item);
        }
    }

    private void OnInputVst3EffectSelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (sender is ComboBox
            {
                Tag: AudioEffectSlotItem slot,
                SelectedItem: Vst3EffectItem effect
            })
        {
            slot.ExternalVst3 = effect.ToReference();
        }
    }

    private void OnVst3EffectPickerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox comboBox ||
            !_configuredVstEffectPickers.Add(comboBox) ||
            comboBox.ItemsSource is not IList effects)
        {
            return;
        }

        var view = new ListCollectionView(effects);
        view.SortDescriptions.Add(
            new SortDescription(nameof(Vst3EffectItem.Vendor), ListSortDirection.Ascending));
        view.SortDescriptions.Add(
            new SortDescription(nameof(Vst3EffectItem.EffectType), ListSortDirection.Ascending));
        view.SortDescriptions.Add(
            new SortDescription(nameof(Vst3EffectItem.DisplayName), ListSortDirection.Ascending));
        view.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(Vst3EffectItem.Vendor)));
        view.GroupDescriptions.Add(
            new PropertyGroupDescription(nameof(Vst3EffectItem.EffectType)));
        comboBox.ItemsSource = view;

        if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is TextBox searchBox)
        {
            searchBox.TextChanged += (_, _) =>
            {
                var query = searchBox.Text;
                view.Filter = item =>
                    item is Vst3EffectItem effect &&
                    effect.MatchesSearch(query);
                view.Refresh();
                if (!comboBox.IsDropDownOpen && searchBox.IsKeyboardFocusWithin)
                {
                    comboBox.IsDropDownOpen = true;
                }
            };
        }
    }

    private void OnVst3EffectPickerDropDownOpened(object sender, EventArgs e)
    {
        if (sender is not ComboBox
            {
                ItemsSource: ListCollectionView view
            } comboBox)
        {
            return;
        }

        if (comboBox.Template.FindName("PART_EditableTextBox", comboBox) is TextBox searchBox)
        {
            if (comboBox.SelectedItem is Vst3EffectItem selectedEffect &&
                string.Equals(
                    searchBox.Text,
                    selectedEffect.DisplayLabel,
                    StringComparison.CurrentCulture))
            {
                view.Filter = null;
                view.Refresh();
                searchBox.SelectAll();
            }
            else if (string.IsNullOrWhiteSpace(searchBox.Text))
            {
                view.Filter = null;
                view.Refresh();
            }
        }
    }

    private void OnEffectSlotDragStart(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: AudioEffectSlotItem slot
            } handle ||
            FindVisualParent<ItemsControl>(handle) is not { } owner ||
            owner.DataContext is null)
        {
            return;
        }

        _effectSlotDragOrigin = e.GetPosition(owner);
        _effectSlotDragCandidate = new EffectSlotDragData(owner.DataContext, slot);
    }

    private void OnEffectSlotPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _effectSlotDragOrigin = null;
            _effectSlotDragCandidate = null;
            return;
        }

        if (sender is not ItemsControl owner ||
            _effectSlotDragOrigin is not { } origin ||
            _effectSlotDragCandidate is not { } candidate ||
            !ReferenceEquals(owner.DataContext, candidate.Owner) ||
            !ExceededDragThreshold(origin, e.GetPosition(owner)))
        {
            return;
        }

        _effectSlotDragOrigin = null;
        _effectSlotDragCandidate = null;
        DragDrop.DoDragDrop(owner, candidate, DragDropEffects.Move);
    }

    private void OnEffectSlotDrop(object sender, DragEventArgs e)
    {
        if (sender is not ItemsControl owner ||
            e.Data.GetData(typeof(EffectSlotDragData)) is not EffectSlotDragData dragged ||
            !ReferenceEquals(owner.DataContext, dragged.Owner))
        {
            return;
        }

        var target = FindItemFromSource<AudioEffectSlotItem>(owner, e.OriginalSource);
        switch (dragged.Owner)
        {
            case AudioInputMonitorItem monitor:
                monitor.MoveEffectTo(
                    dragged.Slot,
                    target is null
                        ? monitor.EffectSlots.Count - 1
                        : monitor.EffectSlots.IndexOf(target));
                break;
            case AudioEffectBusItem bus:
                bus.MoveEffectTo(
                    dragged.Slot,
                    target is null
                        ? bus.EffectSlots.Count - 1
                        : bus.EffectSlots.IndexOf(target));
                break;
        }

        e.Handled = true;
    }

    private void OnPlaylistMouseWheel(object sender, MouseWheelEventArgs e) =>
        ScrollPlaylistOrParent(sender as DependencyObject, e);

    internal static void ScrollPlaylistOrParent(DependencyObject? source, MouseWheelEventArgs e)
    {
        if (source is null)
        {
            return;
        }

        var listScrollViewer = FindVisualChild<ScrollViewer>(source);
        if (listScrollViewer is not null)
        {
            var direction = Math.Sign(e.Delta);
            var canScrollList = direction < 0
                ? listScrollViewer.VerticalOffset < listScrollViewer.ScrollableHeight
                : listScrollViewer.VerticalOffset > 0;

            if (canScrollList)
            {
                listScrollViewer.ScrollToVerticalOffset(
                    Math.Clamp(
                        listScrollViewer.VerticalOffset - e.Delta,
                        0,
                        listScrollViewer.ScrollableHeight));
                e.Handled = true;
                return;
            }
        }

        var parentScrollViewer = FindVisualParent<ScrollViewer>(source);
        if (parentScrollViewer is null)
        {
            return;
        }

        parentScrollViewer.ScrollToVerticalOffset(
            Math.Clamp(
                parentScrollViewer.VerticalOffset - e.Delta,
                0,
                parentScrollViewer.ScrollableHeight));
        e.Handled = true;
    }

    private static bool ExceededDragThreshold(Point origin, Point current) =>
        Math.Abs(current.X - origin.X) >= SystemParameters.MinimumHorizontalDragDistance ||
        Math.Abs(current.Y - origin.Y) >= SystemParameters.MinimumVerticalDragDistance;

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            if (FindVisualChild<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject child)
        where T : DependencyObject
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static T? FindItemFromSource<T>(ItemsControl owner, object source)
        where T : class
    {
        var element = source as DependencyObject;
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: T item })
            {
                return item;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    private sealed record EffectSlotDragData(
        object Owner,
        AudioEffectSlotItem Slot);

    private void OnOpenPlaylistWindowClick(object sender, RoutedEventArgs e)
    {
        if (_playlistWindow is { IsVisible: true })
        {
            _playlistWindow.Activate();
            return;
        }

        _playlistWindow = new PlaylistWindow(_viewModel) { Owner = this };
        _playlistWindow.Closed += (_, _) => _playlistWindow = null;
        _playlistWindow.Show();
    }

    private sealed class YouTubePlaylistPayload
    {
        public string Title { get; set; } = string.Empty;
        public List<YouTubePlaylistItemPayload> Items { get; set; } = [];
    }

    private sealed class YouTubePlaylistItemPayload
    {
        public string VideoId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? ThumbnailUrl { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
