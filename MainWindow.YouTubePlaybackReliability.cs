using System.ComponentModel;
using System.Text.Json;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using DrumPracticeStudio.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    private bool _youtubePlaybackReliabilityAttached;
    private string? _managedYouTubeVideoId;
    private string? _managedLoadedYouTubeVideoId;
    private string? _lastAdvancedManagedYouTubeVideoId;
    private long _managedYouTubeGeneration;
    private long _managedYouTubeAudioRecoveryVersion;
    private int _managedUnexpectedAdvanceInProgress;
    private CoreWebView2? _youtubeReliabilityCore;
    private Slider? _youtubeTransportSlider;

    private void AttachYouTubePlaybackReliabilityFixes()
    {
        if (_youtubePlaybackReliabilityAttached)
        {
            return;
        }

        _youtubePlaybackReliabilityAttached = true;

        // Sustituimos el manejador inicial para que toda reproducción solicitada por una playlist
        // pase por una URL canónica sin parámetros list/index y por una única secuencia de arranque.
        _viewModel.YouTubePlaybackRequested -= OnYouTubePlaybackRequested;
        _viewModel.YouTubePlaybackRequested += OnManagedYouTubePlaybackRequested;
        _viewModel.YouTubeSeekRequested += OnManagedYouTubeSeekRequested;
        _viewModel.PropertyChanged += OnYouTubeReliabilityViewModelPropertyChanged;
        _viewModel.AttachYouTubeTransport();

        YouTubeWebView.NavigationStarting += OnManagedYouTubeNavigationStarting;
        YouTubeWebView.NavigationCompleted += OnManagedYouTubeNavigationCompleted;
        YouTubeWebView.CoreWebView2InitializationCompleted += OnYouTubeReliabilityInitializationCompleted;
        Closed += OnYouTubeReliabilityClosed;

        AttachYouTubeTransportSlider();
        AttachYouTubeReliabilityCoreHandlers();
    }

    private void OnYouTubeReliabilityInitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess)
        {
            AttachYouTubeReliabilityCoreHandlers();
        }
    }

    private void AttachYouTubeReliabilityCoreHandlers()
    {
        if (YouTubeWebView.CoreWebView2 is not { } core ||
            ReferenceEquals(_youtubeReliabilityCore, core))
        {
            return;
        }

        DetachYouTubeReliabilityCoreHandlers();
        _youtubeReliabilityCore = core;
        core.WebMessageReceived += OnYouTubeReliabilityWebMessageReceived;
        core.SourceChanged += OnManagedYouTubeSourceChanged;
    }

    private void DetachYouTubeReliabilityCoreHandlers()
    {
        if (_youtubeReliabilityCore is not { } core)
        {
            return;
        }

        try
        {
            core.WebMessageReceived -= OnYouTubeReliabilityWebMessageReceived;
            core.SourceChanged -= OnManagedYouTubeSourceChanged;
        }
        catch (ObjectDisposedException)
        {
        }

        _youtubeReliabilityCore = null;
    }

    private void OnYouTubeReliabilityClosed(object? sender, EventArgs eventArgs)
    {
        _viewModel.YouTubePlaybackRequested -= OnManagedYouTubePlaybackRequested;
        _viewModel.YouTubeSeekRequested -= OnManagedYouTubeSeekRequested;
        _viewModel.PropertyChanged -= OnYouTubeReliabilityViewModelPropertyChanged;
        YouTubeWebView.NavigationStarting -= OnManagedYouTubeNavigationStarting;
        YouTubeWebView.NavigationCompleted -= OnManagedYouTubeNavigationCompleted;
        YouTubeWebView.CoreWebView2InitializationCompleted -= OnYouTubeReliabilityInitializationCompleted;
        DetachYouTubeReliabilityCoreHandlers();
        DetachYouTubeTransportSlider();
        Closed -= OnYouTubeReliabilityClosed;
    }

    private async void OnManagedYouTubePlaybackRequested(
        object? sender,
        YouTubePlaybackRequest request)
    {
        var requestVersion = ++_youtubePlaybackRequestVersion;
        var canonicalRequest = request with
        {
            Uri = YouTubeNavigationService.CreateWatchUri(request.VideoId)
        };

        _pendingYouTubePlayback = canonicalRequest;
        _managedYouTubeVideoId = request.VideoId;
        if (!string.Equals(
                _lastAdvancedManagedYouTubeVideoId,
                request.VideoId,
                StringComparison.Ordinal))
        {
            _lastAdvancedManagedYouTubeVideoId = null;
        }

        Interlocked.Increment(ref _managedYouTubeGeneration);
        Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);
        Interlocked.Increment(ref _youtubeAudioProbeVersion);
        _viewModel.BeginYouTubeTransport();

        // La selección desde la playlist debe iniciar el vídeo por sí sola. Mostrar la sección de
        // YouTube además garantiza que WebView2 tenga una superficie visible para crear el player.
        _viewModel.OpenYouTubePage();
        await EnsureYouTubeReadyAsync();
        if (requestVersion != _youtubePlaybackRequestVersion ||
            YouTubeWebView.CoreWebView2 is not { } core)
        {
            return;
        }

        AttachYouTubeReliabilityCoreHandlers();
        core.IsMuted = false;
        try
        {
            await core.ExecuteScriptAsync(
                "clearInterval(window.__dpsManagedEndTimer); " +
                "clearInterval(window.__dpsManagedStartTimer); " +
                "window.__dpsManagedVideoId = null; " +
                "document.querySelector('video')?.pause();");
        }
        catch (InvalidOperationException)
        {
            // La navegación anterior puede haber destruido el documento.
        }

        // La captura anterior debe abandonar el mezclador antes de crear la del vídeo siguiente.
        await _viewModel.ResetYouTubeAudioRoutingAsync();
        if (requestVersion != _youtubePlaybackRequestVersion)
        {
            return;
        }

        YouTubeStatusText.Text = $"Cargando desde la playlist de Drumless: {request.Title}";
        YouTubeWebView.Source = canonicalRequest.Uri;
    }

    private void OnManagedYouTubeNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        if (!TryGetUnexpectedManagedVideo(eventArgs.Uri, out _))
        {
            return;
        }

        // YouTube intenta abrir el siguiente elemento de su propia cola. Se cancela antes de que
        // sustituya el vídeo y se deja que PlaybackNavigator elija el siguiente elemento de Drumless.
        eventArgs.Cancel = true;
        HandleUnexpectedManagedYouTubeAdvance();
    }

    private void OnManagedYouTubeSourceChanged(
        object? sender,
        CoreWebView2SourceChangedEventArgs eventArgs)
    {
        if (_youtubeReliabilityCore?.Source is not { Length: > 0 } source ||
            !TryGetUnexpectedManagedVideo(source, out _))
        {
            return;
        }

        // Las transiciones SPA de YouTube pueden no generar una navegación cancelable. Esta segunda
        // barrera detecta el cambio de URL y recupera inmediatamente la cola interna.
        HandleUnexpectedManagedYouTubeAdvance();
    }

    private bool TryGetUnexpectedManagedVideo(string? uriText, out string actualVideoId)
    {
        actualVideoId = string.Empty;
        return _managedYouTubeVideoId is { Length: > 0 } expectedVideoId &&
               Uri.TryCreate(uriText, UriKind.Absolute, out var uri) &&
               YouTubeNavigationService.TryGetVideoId(uri, out actualVideoId) &&
               !string.Equals(actualVideoId, expectedVideoId, StringComparison.Ordinal);
    }

    private void HandleUnexpectedManagedYouTubeAdvance()
    {
        var expectedVideoId = _managedYouTubeVideoId;
        if (expectedVideoId is null)
        {
            return;
        }

        // Si Drumless ya pidió el siguiente vídeo pero WebView2 todavía muestra el anterior, no hay
        // que avanzar otra vez: basta con imponer la URL canónica que ya estaba pendiente.
        if (!string.Equals(
                _managedLoadedYouTubeVideoId,
                expectedVideoId,
                StringComparison.Ordinal))
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                if (string.Equals(
                        _managedYouTubeVideoId,
                        expectedVideoId,
                        StringComparison.Ordinal))
                {
                    YouTubeWebView.Source = YouTubeNavigationService.CreateWatchUri(expectedVideoId);
                }
            });
            return;
        }

        if (string.Equals(
                _lastAdvancedManagedYouTubeVideoId,
                expectedVideoId,
                StringComparison.Ordinal) ||
            Interlocked.CompareExchange(ref _managedUnexpectedAdvanceInProgress, 1, 0) != 0)
        {
            return;
        }

        _lastAdvancedManagedYouTubeVideoId = expectedVideoId;
        _ = AdvanceInternalQueueAfterYouTubeAttemptAsync(expectedVideoId);
    }

    private async Task AdvanceInternalQueueAfterYouTubeAttemptAsync(string completedVideoId)
    {
        try
        {
            await _viewModel.HandleYouTubeEndedAsync(completedVideoId);
        }
        finally
        {
            Volatile.Write(ref _managedUnexpectedAdvanceInProgress, 0);
        }
    }

    private async void OnManagedYouTubeNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess ||
            _managedYouTubeVideoId is not { Length: > 0 } expectedVideoId ||
            YouTubeWebView.CoreWebView2 is not { } core ||
            !YouTubeNavigationService.TryGetVideoId(YouTubeWebView.Source, out var currentVideoId) ||
            !string.Equals(currentVideoId, expectedVideoId, StringComparison.Ordinal))
        {
            return;
        }

        _managedLoadedYouTubeVideoId = expectedVideoId;
        var expectedJson = JsonSerializer.Serialize(expectedVideoId);
        try
        {
            await core.ExecuteScriptAsync(
                $$"""
                (() => {
                  const expectedVideoId = {{expectedJson}};
                  window.__dpsManagedVideoId = expectedVideoId;
                  window.__dpsManagedEndSent = false;
                  clearInterval(window.__dpsManagedEndTimer);
                  clearInterval(window.__dpsManagedStartTimer);

                  const currentVideoId = () =>
                    new URL(location.href).searchParams.get('v') || '';

                  const disableNativeAutoplay = () => {
                    const toggle = document.querySelector('.ytp-autonav-toggle-button');
                    if (toggle?.getAttribute('aria-checked') === 'true') {
                      toggle.click();
                    }
                  };

                  const postPosition = video => {
                    const duration = Number(video.duration);
                    chrome.webview.postMessage({
                      type: 'managed-video-position',
                      videoId: expectedVideoId,
                      seconds: Number(video.currentTime || 0),
                      duration: Number.isFinite(duration) ? duration : 0,
                      playing: !video.paused && !video.ended
                    });
                  };

                  const notifyManagedEnd = video => {
                    if (window.__dpsManagedVideoId !== expectedVideoId ||
                        window.__dpsManagedEndSent ||
                        currentVideoId() !== expectedVideoId) {
                      return;
                    }

                    window.__dpsManagedEndSent = true;
                    try { video.pause(); } catch (_) {}
                    postPosition(video);
                    chrome.webview.postMessage({
                      type: 'video-ended',
                      videoId: expectedVideoId,
                      source: 'drumless-managed-playlist'
                    });
                  };

                  const prepareVideo = () => {
                    const video = document.querySelector('video');
                    if (!video) return null;
                    video.autoplay = false;
                    video.loop = false;
                    if (video.__dpsManagedEndId !== expectedVideoId) {
                      video.__dpsManagedEndId = expectedVideoId;
                      video.addEventListener('ended', () => notifyManagedEnd(video));
                      video.addEventListener('timeupdate', () => postPosition(video));
                      video.addEventListener('durationchange', () => postPosition(video));
                    }
                    return video;
                  };

                  disableNativeAutoplay();
                  prepareVideo();
                  let lastPositionNotice = 0;
                  window.__dpsManagedEndTimer = setInterval(() => {
                    disableNativeAutoplay();
                    const video = prepareVideo();
                    if (!video || currentVideoId() !== expectedVideoId) return;

                    const now = performance.now();
                    if (now - lastPositionNotice >= 100) {
                      lastPositionNotice = now;
                      postPosition(video);
                    }

                    const duration = Number(video.duration);
                    const remaining = duration - Number(video.currentTime || 0);
                    if (video.ended ||
                        (!video.paused && Number.isFinite(duration) && duration > 0 && remaining <= 0.18)) {
                      notifyManagedEnd(video);
                    }
                  }, 50);

                  // La petición nace de un clic en la playlist, pero la navegación puede perder la
                  // activación de usuario. Se reintenta el play y, sólo si el navegador lo exige,
                  // se arranca silenciado durante un instante antes de recuperar el volumen.
                  let startAttempts = 0;
                  window.__dpsManagedStartTimer = setInterval(async () => {
                    if (currentVideoId() !== expectedVideoId) {
                      clearInterval(window.__dpsManagedStartTimer);
                      return;
                    }
                    const video = prepareVideo();
                    if (!video) return;
                    startAttempts += 1;
                    try {
                      video.muted = false;
                      await video.play();
                      clearInterval(window.__dpsManagedStartTimer);
                      postPosition(video);
                    } catch (_) {
                      if (startAttempts === 10) {
                        try {
                          video.muted = true;
                          await video.play();
                          setTimeout(() => { video.muted = false; }, 120);
                          clearInterval(window.__dpsManagedStartTimer);
                          postPosition(video);
                        } catch (_) {}
                      }
                    }
                    if (startAttempts >= 80) {
                      clearInterval(window.__dpsManagedStartTimer);
                    }
                  }, 125);
                })();
                """);
        }
        catch (InvalidOperationException)
        {
            // YouTube puede sustituir el documento mientras termina NavigationCompleted.
        }
    }

    private async void OnYouTubeReliabilityWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        string? type;
        string? videoId;
        bool playing;
        double seconds;
        double duration;
        try
        {
            using var document = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            var root = document.RootElement;
            type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            videoId = root.TryGetProperty("videoId", out var videoIdElement)
                ? videoIdElement.GetString()
                : null;
            playing = root.TryGetProperty("playing", out var playingElement) &&
                      playingElement.ValueKind is JsonValueKind.True &&
                      playingElement.GetBoolean();
            seconds = root.TryGetProperty("seconds", out var secondsElement) &&
                      secondsElement.TryGetDouble(out var parsedSeconds)
                ? parsedSeconds
                : 0d;
            duration = root.TryGetProperty("duration", out var durationElement) &&
                       durationElement.TryGetDouble(out var parsedDuration)
                ? parsedDuration
                : 0d;
        }
        catch (JsonException)
        {
            return;
        }

        if (!string.Equals(videoId, _managedYouTubeVideoId, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(type, "managed-video-position", StringComparison.Ordinal))
        {
            _viewModel.UpdateYouTubeTransport(seconds, duration, playing);
            return;
        }

        if (!string.Equals(type, "video-state", StringComparison.Ordinal))
        {
            return;
        }

        _viewModel.SetYouTubeTransportPlaying(playing);
        if (!playing)
        {
            return;
        }

        if (_youtubeDirectOutputAttached)
        {
            RequestYouTubeDirectOutputRouting();
            return;
        }

        var generation = Volatile.Read(ref _managedYouTubeGeneration);
        var recoveryVersion = Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(attempt == 0 ? 350 : 450);
            if (generation != Volatile.Read(ref _managedYouTubeGeneration) ||
                recoveryVersion != Volatile.Read(ref _managedYouTubeAudioRecoveryVersion) ||
                !string.Equals(videoId, _managedYouTubeVideoId, StringComparison.Ordinal))
            {
                return;
            }

            if (_viewModel.IsYouTubeAudioRouted)
            {
                return;
            }

            await EnsureYouTubeAudioRoutingAsync();
        }

        if (!_viewModel.IsYouTubeAudioRouted && YouTubeWebView.CoreWebView2 is { } core)
        {
            core.IsMuted = false;
        }
    }

    private async void OnManagedYouTubeSeekRequested(object? sender, double seconds)
    {
        if (YouTubeWebView.CoreWebView2 is not { } core ||
            _managedYouTubeVideoId is not { Length: > 0 } expectedVideoId)
        {
            return;
        }

        var secondsJson = JsonSerializer.Serialize(Math.Max(0d, seconds));
        var expectedJson = JsonSerializer.Serialize(expectedVideoId);
        try
        {
            await core.ExecuteScriptAsync(
                $$"""
                (() => {
                  const expectedVideoId = {{expectedJson}};
                  if ((new URL(location.href).searchParams.get('v') || '') !== expectedVideoId) return;
                  const video = document.querySelector('video');
                  if (!video) return;
                  video.currentTime = {{secondsJson}};
                  chrome.webview.postMessage({
                    type: 'managed-video-position',
                    videoId: expectedVideoId,
                    seconds: video.currentTime,
                    duration: Number.isFinite(video.duration) ? video.duration : 0,
                    playing: !video.paused && !video.ended
                  });
                })();
                """);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void AttachYouTubeTransportSlider()
    {
        var slider = FindTransportSlider(this);
        if (slider is null || ReferenceEquals(slider, _youtubeTransportSlider))
        {
            return;
        }

        DetachYouTubeTransportSlider();
        _youtubeTransportSlider = slider;
        slider.PreviewMouseLeftButtonDown += OnYouTubeTransportSliderBeginSeek;
        slider.PreviewMouseLeftButtonUp += OnYouTubeTransportSliderCommitSeek;
        slider.PreviewKeyDown += OnYouTubeTransportSliderKeyDown;
        slider.PreviewKeyUp += OnYouTubeTransportSliderKeyUp;
    }

    private void DetachYouTubeTransportSlider()
    {
        if (_youtubeTransportSlider is not { } slider)
        {
            return;
        }

        slider.PreviewMouseLeftButtonDown -= OnYouTubeTransportSliderBeginSeek;
        slider.PreviewMouseLeftButtonUp -= OnYouTubeTransportSliderCommitSeek;
        slider.PreviewKeyDown -= OnYouTubeTransportSliderKeyDown;
        slider.PreviewKeyUp -= OnYouTubeTransportSliderKeyUp;
        _youtubeTransportSlider = null;
    }

    private void OnYouTubeTransportSliderBeginSeek(object sender, MouseButtonEventArgs eventArgs) =>
        _viewModel.BeginYouTubeTransportSeek();

    private void OnYouTubeTransportSliderCommitSeek(object sender, MouseButtonEventArgs eventArgs)
    {
        if (sender is Slider slider)
        {
            _viewModel.CommitYouTubeTransportSeek(slider.Value);
        }
    }

    private void OnYouTubeTransportSliderKeyDown(object sender, KeyEventArgs eventArgs)
    {
        if (IsTransportSeekKey(eventArgs.Key))
        {
            _viewModel.BeginYouTubeTransportSeek();
        }
    }

    private void OnYouTubeTransportSliderKeyUp(object sender, KeyEventArgs eventArgs)
    {
        if (sender is Slider slider && IsTransportSeekKey(eventArgs.Key))
        {
            _viewModel.CommitYouTubeTransportSeek(slider.Value);
        }
    }

    private static bool IsTransportSeekKey(Key key) => key is
        Key.Left or Key.Right or Key.Home or Key.End or Key.PageUp or Key.PageDown;

    private static Slider? FindTransportSlider(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is Slider slider &&
                string.Equals(
                    AutomationProperties.GetName(slider),
                    "Posición de reproducción",
                    StringComparison.Ordinal))
            {
                return slider;
            }

            if (FindTransportSlider(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void OnYouTubeReliabilityViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(MainViewModel.AudioOutputStatus) ||
            _managedYouTubeVideoId is null ||
            YouTubeWebView.CoreWebView2 is null)
        {
            return;
        }

        _ = Dispatcher.InvokeAsync(() => _ = RestartManagedYouTubeAudioAfterOutputChangeAsync());
    }

    private async Task RestartManagedYouTubeAudioAfterOutputChangeAsync()
    {
        if (_youtubeDirectOutputAttached)
        {
            RequestYouTubeDirectOutputRouting();
            return;
        }

        var generation = Volatile.Read(ref _managedYouTubeGeneration);
        var recoveryVersion = Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);
        Interlocked.Increment(ref _youtubeAudioProbeVersion);

        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            core.IsMuted = false;
        }

        await _viewModel.ResetYouTubeAudioRoutingAsync();
        if (generation != Volatile.Read(ref _managedYouTubeGeneration) ||
            recoveryVersion != Volatile.Read(ref _managedYouTubeAudioRecoveryVersion) ||
            _managedYouTubeVideoId is null)
        {
            return;
        }

        await EnsureYouTubeAudioRoutingAsync();
        if (!_viewModel.IsYouTubeAudioRouted && YouTubeWebView.CoreWebView2 is { } activeCore)
        {
            activeCore.IsMuted = false;
        }
    }
}
