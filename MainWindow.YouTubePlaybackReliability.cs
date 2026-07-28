using System.ComponentModel;
using System.Text.Json;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using DrumPracticeStudio.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    private bool _youtubePlaybackReliabilityAttached;
    private bool _youtubeReliabilityWebMessagesAttached;
    private string? _managedYouTubeVideoId;
    private long _managedYouTubeGeneration;
    private long _managedYouTubeAudioRecoveryVersion;

    private void AttachYouTubePlaybackReliabilityFixes()
    {
        if (_youtubePlaybackReliabilityAttached)
        {
            return;
        }

        _youtubePlaybackReliabilityAttached = true;
        _viewModel.YouTubePlaybackRequested += OnManagedYouTubePlaybackRequested;
        _viewModel.PropertyChanged += OnYouTubeReliabilityViewModelPropertyChanged;
        YouTubeWebView.NavigationCompleted += OnManagedYouTubeNavigationCompleted;
        YouTubeWebView.CoreWebView2InitializationCompleted += OnYouTubeReliabilityInitializationCompleted;
        Closed += OnYouTubeReliabilityClosed;
        AttachYouTubeReliabilityWebMessages();
    }

    private void OnYouTubeReliabilityInitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess)
        {
            AttachYouTubeReliabilityWebMessages();
        }
    }

    private void AttachYouTubeReliabilityWebMessages()
    {
        if (_youtubeReliabilityWebMessagesAttached || YouTubeWebView.CoreWebView2 is not { } core)
        {
            return;
        }

        _youtubeReliabilityWebMessagesAttached = true;
        core.WebMessageReceived += OnYouTubeReliabilityWebMessageReceived;
    }

    private void OnYouTubeReliabilityClosed(object? sender, EventArgs eventArgs)
    {
        _viewModel.YouTubePlaybackRequested -= OnManagedYouTubePlaybackRequested;
        _viewModel.PropertyChanged -= OnYouTubeReliabilityViewModelPropertyChanged;
        YouTubeWebView.NavigationCompleted -= OnManagedYouTubeNavigationCompleted;
        YouTubeWebView.CoreWebView2InitializationCompleted -= OnYouTubeReliabilityInitializationCompleted;
        if (_youtubeReliabilityWebMessagesAttached)
        {
            try
            {
                YouTubeWebView.CoreWebView2?.WebMessageReceived -= OnYouTubeReliabilityWebMessageReceived;
            }
            catch (ObjectDisposedException)
            {
            }
        }
        Closed -= OnYouTubeReliabilityClosed;
    }

    private async void OnManagedYouTubePlaybackRequested(
        object? sender,
        YouTubePlaybackRequest request)
    {
        _managedYouTubeVideoId = request.VideoId;
        Interlocked.Increment(ref _managedYouTubeGeneration);
        Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);
        Interlocked.Increment(ref _youtubeAudioProbeVersion);

        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            core.IsMuted = false;
            try
            {
                await core.ExecuteScriptAsync(
                    "clearInterval(window.__dpsManagedEndTimer); window.__dpsManagedVideoId = null;");
            }
            catch (InvalidOperationException)
            {
                // La navegación puede haber destruido el documento anterior.
            }
        }

        // Esperamos a que la captura anterior haya salido del mezclador. De ese modo un cierre
        // retrasado no puede alcanzar y eliminar la captura recién creada para el vídeo siguiente.
        await _viewModel.ResetYouTubeAudioRoutingAsync();
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

                  const currentVideoId = () =>
                    new URL(location.href).searchParams.get('v') || '';

                  const disableNativeAutoplay = () => {
                    const toggle = document.querySelector('.ytp-autonav-toggle-button');
                    if (toggle?.getAttribute('aria-checked') === 'true') {
                      toggle.click();
                    }
                  };

                  const notifyManagedEnd = video => {
                    if (window.__dpsManagedVideoId !== expectedVideoId ||
                        window.__dpsManagedEndSent ||
                        currentVideoId() !== expectedVideoId) {
                      return;
                    }

                    window.__dpsManagedEndSent = true;
                    try { video.pause(); } catch (_) {}
                    chrome.webview.postMessage({
                      type: 'video-ended',
                      videoId: expectedVideoId,
                      source: 'drumless-managed-playlist'
                    });
                  };

                  const attachEndedHandler = () => {
                    const video = document.querySelector('video');
                    if (!video || video.__dpsManagedEndId === expectedVideoId) return;
                    video.__dpsManagedEndId = expectedVideoId;
                    video.autoplay = false;
                    video.loop = false;
                    video.addEventListener('ended', () => notifyManagedEnd(video));
                  };

                  attachEndedHandler();
                  disableNativeAutoplay();
                  window.__dpsManagedEndTimer = setInterval(() => {
                    attachEndedHandler();
                    disableNativeAutoplay();
                    const video = document.querySelector('video');
                    if (!video || currentVideoId() !== expectedVideoId) return;
                    const duration = Number(video.duration);
                    const remaining = duration - Number(video.currentTime || 0);
                    if (video.ended ||
                        (!video.paused && Number.isFinite(duration) && duration > 0 && remaining <= 0.12)) {
                      notifyManagedEnd(video);
                    }
                  }, 50);
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
        }
        catch (JsonException)
        {
            return;
        }

        if (!string.Equals(type, "video-state", StringComparison.Ordinal) ||
            !playing ||
            !string.Equals(videoId, _managedYouTubeVideoId, StringComparison.Ordinal))
        {
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

            // El manejador normal también lo intenta. Las pasadas adicionales cubren el momento en
            // que el intento anterior todavía estaba cerrando la captura del vídeo precedente.
            await EnsureYouTubeAudioRoutingAsync();
        }

        // Si Windows no admite la captura por proceso, el navegador queda expresamente sin silenciar
        // para que el vídeo siga oyéndose por la salida normal en lugar de quedarse mudo.
        if (!_viewModel.IsYouTubeAudioRouted && YouTubeWebView.CoreWebView2 is { } core)
        {
            core.IsMuted = false;
        }
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
