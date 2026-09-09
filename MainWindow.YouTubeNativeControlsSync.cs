using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace DrumPracticeStudio;

internal static class YouTubeNativeControlsSyncBootstrapper
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnMainWindowLoaded),
            handledEventsToo: true);
    }

    private static void OnMainWindowLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is MainWindow window)
        {
            window.AttachYouTubeNativeControlsSync();
        }
    }
}

public partial class MainWindow
{
    private bool _youtubeNativeControlsSyncAttached;
    private CoreWebView2? _youtubeNativeControlsSyncCore;
    private string? _youtubeNativeControlsEarlyScriptId;

    internal void AttachYouTubeNativeControlsSync()
    {
        if (_youtubeNativeControlsSyncAttached)
        {
            return;
        }

        _youtubeNativeControlsSyncAttached = true;

        // El WebView vuelve a ser una superficie interactiva. La protección antigua sigue existiendo
        // para documentos que no hayan recibido el script temprano, pero a partir de la siguiente
        // navegación el guard queda marcado antes de que pueda instalar los bloqueos físicos.
        YouTubeWebView.Focusable = true;
        YouTubeWebView.IsHitTestVisible = true;
        KeyboardNavigation.SetTabNavigation(YouTubeWebView, KeyboardNavigationMode.Continue);

        YouTubeWebView.CoreWebView2InitializationCompleted +=
            OnYouTubeNativeControlsInitializationCompleted;
        YouTubeWebView.NavigationCompleted += OnYouTubeNativeControlsNavigationCompleted;
        Closed += OnYouTubeNativeControlsClosed;

        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            _ = ConfigureYouTubeNativeControlsCoreAsync(core);
        }
    }

    private async void OnYouTubeNativeControlsInitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess && YouTubeWebView.CoreWebView2 is { } core)
        {
            await ConfigureYouTubeNativeControlsCoreAsync(core);
        }
    }

    private async void OnYouTubeNativeControlsNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess && YouTubeWebView.CoreWebView2 is { } core)
        {
            await InstallYouTubeNativeControlsSyncAsync(core);
        }
    }

    private async Task ConfigureYouTubeNativeControlsCoreAsync(CoreWebView2 core)
    {
        if (!ReferenceEquals(_youtubeNativeControlsSyncCore, core))
        {
            DetachYouTubeNativeControlsCore();
            _youtubeNativeControlsSyncCore = core;
            core.WebMessageReceived += OnYouTubeNativeControlsWebMessageReceived;

            // MainWindow.GlobalTransportProtectedYouTube usa este mismo flag como guard. Al
            // establecerlo al crear cada documento evitamos que registre sus listeners que
            // bloqueaban ratón/touch/teclado sobre el reproductor. No se toca sinkId ni el
            // enrutado de audio.
            try
            {
                _youtubeNativeControlsEarlyScriptId =
                    await core.AddScriptToExecuteOnDocumentCreatedAsync(
                        "window.__dpsPhysicalInputBlocked = true;");
            }
            catch (Exception exception) when (exception is
                InvalidOperationException or
                ObjectDisposedException or
                System.Runtime.InteropServices.COMException)
            {
                return;
            }
        }

        await InstallYouTubeNativeControlsSyncAsync(core);
    }

    private static async Task InstallYouTubeNativeControlsSyncAsync(CoreWebView2 core)
    {
        try
        {
            await core.ExecuteScriptAsync(YouTubeNativeControlsSyncScript);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            ObjectDisposedException or
            System.Runtime.InteropServices.COMException)
        {
            // YouTube puede sustituir el documento mientras se instala la sincronización.
        }
    }

    private void OnYouTubeNativeControlsWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using var document = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement))
            {
                return;
            }

            var type = typeElement.GetString();
            if (string.Equals(type, "native-youtube-command", StringComparison.Ordinal))
            {
                var action = root.TryGetProperty("action", out var actionElement)
                    ? actionElement.GetString()
                    : null;
                var command = string.Equals(action, "previous", StringComparison.Ordinal)
                    ? _viewModel.PreviousTrackCommand
                    : string.Equals(action, "next", StringComparison.Ordinal)
                        ? _viewModel.NextTrackCommand
                        : null;
                if (command?.CanExecute(null) == true)
                {
                    command.Execute(null);
                }
                return;
            }

            if (!string.Equals(type, "native-youtube-state", StringComparison.Ordinal))
            {
                return;
            }

            var videoId = root.TryGetProperty("videoId", out var videoIdElement)
                ? videoIdElement.GetString()
                : null;
            var seconds = root.TryGetProperty("seconds", out var secondsElement) &&
                          secondsElement.TryGetDouble(out var parsedSeconds)
                ? parsedSeconds
                : 0d;
            var duration = root.TryGetProperty("duration", out var durationElement) &&
                           durationElement.TryGetDouble(out var parsedDuration)
                ? parsedDuration
                : 0d;
            var playing = root.TryGetProperty("playing", out var playingElement) &&
                          playingElement.ValueKind == JsonValueKind.True;

            _viewModel.UpdateYouTubeTransport(videoId, seconds, duration, playing);
        }
        catch (JsonException)
        {
        }
    }

    private void OnYouTubeNativeControlsClosed(object? sender, EventArgs eventArgs)
    {
        YouTubeWebView.CoreWebView2InitializationCompleted -=
            OnYouTubeNativeControlsInitializationCompleted;
        YouTubeWebView.NavigationCompleted -= OnYouTubeNativeControlsNavigationCompleted;
        Closed -= OnYouTubeNativeControlsClosed;
        DetachYouTubeNativeControlsCore();
    }

    private void DetachYouTubeNativeControlsCore()
    {
        if (_youtubeNativeControlsSyncCore is not { } core)
        {
            return;
        }

        try
        {
            core.WebMessageReceived -= OnYouTubeNativeControlsWebMessageReceived;
            if (!string.IsNullOrWhiteSpace(_youtubeNativeControlsEarlyScriptId))
            {
                core.RemoveScriptToExecuteOnDocumentCreated(_youtubeNativeControlsEarlyScriptId);
            }
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            ObjectDisposedException or
            System.Runtime.InteropServices.COMException)
        {
        }

        _youtubeNativeControlsEarlyScriptId = null;
        _youtubeNativeControlsSyncCore = null;
    }

    private const string YouTubeNativeControlsSyncScript =
        """
        (() => {
          window.__dpsPhysicalInputBlocked = true;
          if (window.__dpsNativeControlsSyncInstalled) return;
          window.__dpsNativeControlsSyncInstalled = true;

          const currentVideoId = () => {
            try {
              return new URL(location.href).searchParams.get('v') || '';
            } catch (_) {
              return '';
            }
          };

          const postState = video => {
            if (!video) return;
            const duration = Number(video.duration);
            chrome.webview.postMessage({
              type: 'native-youtube-state',
              videoId: window.__dpsManagedVideoId || currentVideoId(),
              seconds: Number(video.currentTime || 0),
              duration: Number.isFinite(duration) ? duration : 0,
              playing: !video.paused && !video.ended
            });
          };

          const hookVideo = () => {
            const video = document.querySelector('video');
            if (!video || video.__dpsNativeControlsSync) return video;
            video.__dpsNativeControlsSync = true;
            for (const eventName of [
              'play', 'pause', 'timeupdate', 'durationchange',
              'seeking', 'seeked', 'ended'
            ]) {
              video.addEventListener(eventName, () => postState(video));
            }
            postState(video);
            return video;
          };

          // En una reproducción gestionada por Drumless, los botones nativos siguiente/anterior
          // deben mover la cola de Drumless, no navegar por las recomendaciones de YouTube.
          window.addEventListener('click', event => {
            if (!event.isTrusted || !window.__dpsManagedVideoId) return;
            const target = event.target;
            if (!(target instanceof Element)) return;

            let action = null;
            if (target.closest('.ytp-prev-button')) action = 'previous';
            else if (target.closest('.ytp-next-button')) action = 'next';
            if (!action) return;

            event.preventDefault();
            event.stopImmediatePropagation();
            chrome.webview.postMessage({
              type: 'native-youtube-command',
              action
            });
          }, true);

          hookVideo();
          const observer = new MutationObserver(hookVideo);
          observer.observe(document.documentElement, {
            childList: true,
            subtree: true
          });
        })();
        """;
}
