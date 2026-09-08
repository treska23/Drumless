using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    private bool _youtubeRecordingBridgeAttached;
    private CoreWebView2? _youtubeRecordingCore;

    [ModuleInitializer]
    internal static void InitializeYouTubeRecordingBridge()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnMainWindowLoadedForYouTubeRecording));
    }

    private static void OnMainWindowLoadedForYouTubeRecording(
        object sender,
        RoutedEventArgs eventArgs)
    {
        if (sender is MainWindow window)
        {
            window.AttachYouTubeRecordingBridge();
        }
    }

    private void AttachYouTubeRecordingBridge()
    {
        if (_youtubeRecordingBridgeAttached)
        {
            AttachYouTubeRecordingCore();
            return;
        }

        _youtubeRecordingBridgeAttached = true;
        YouTubeWebView.CoreWebView2InitializationCompleted +=
            OnYouTubeRecordingCoreInitializationCompleted;
        Closed += OnYouTubeRecordingBridgeClosed;
        AttachYouTubeRecordingCore();
    }

    private void OnYouTubeRecordingCoreInitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess)
        {
            AttachYouTubeRecordingCore();
        }
    }

    private void AttachYouTubeRecordingCore()
    {
        var core = YouTubeWebView.CoreWebView2;
        if (core is null)
        {
            _viewModel.SetYouTubeBrowserProcessId(null);
            _viewModel.SetYouTubePlaybackAudible(false);
            return;
        }

        if (!ReferenceEquals(core, _youtubeRecordingCore))
        {
            DetachYouTubeRecordingCore();
            _youtubeRecordingCore = core;
            core.IsMutedChanged += OnYouTubeRecordingMutedChanged;
        }

        UpdateYouTubeRecordingProcessId();

        // Otros handlers del mismo Loaded/InitializationCompleted pueden estar terminando todavía
        // de aplicar setSinkId y el mute de protección. Comprobamos el estado final en Dispatcher.
        _ = Dispatcher.BeginInvoke(new Action(UpdateYouTubeRecordingAudibility));
    }

    private void DetachYouTubeRecordingCore()
    {
        if (_youtubeRecordingCore is not { } core)
        {
            return;
        }

        try
        {
            core.IsMutedChanged -= OnYouTubeRecordingMutedChanged;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or ObjectDisposedException)
        {
        }
        _youtubeRecordingCore = null;
    }

    private void OnYouTubeRecordingMutedChanged(object? sender, object eventArgs)
    {
        if (sender is CoreWebView2 core)
        {
            UpdateYouTubeRecordingAudibility(core);
        }
    }

    private void UpdateYouTubeRecordingAudibility()
    {
        if (_youtubeRecordingCore is { } core)
        {
            UpdateYouTubeRecordingAudibility(core);
        }
        else
        {
            _viewModel.SetYouTubePlaybackAudible(false);
        }
    }

    private void UpdateYouTubeRecordingAudibility(CoreWebView2 core)
    {
        try
        {
            _viewModel.SetYouTubePlaybackAudible(!core.IsMuted);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or ObjectDisposedException)
        {
            _viewModel.SetYouTubePlaybackAudible(false);
        }
    }

    private void UpdateYouTubeRecordingProcessId()
    {
        try
        {
            var processId = YouTubeWebView.CoreWebView2?.BrowserProcessId ?? 0;
            _viewModel.SetYouTubeBrowserProcessId(processId == 0 ? null : processId);
        }
        catch (InvalidOperationException)
        {
            _viewModel.SetYouTubeBrowserProcessId(null);
        }
    }

    private void OnYouTubeRecordingBridgeClosed(object? sender, EventArgs eventArgs)
    {
        YouTubeWebView.CoreWebView2InitializationCompleted -=
            OnYouTubeRecordingCoreInitializationCompleted;
        Closed -= OnYouTubeRecordingBridgeClosed;
        DetachYouTubeRecordingCore();
        _viewModel.SetYouTubePlaybackAudible(false);
        _viewModel.SetYouTubeBrowserProcessId(null);
    }
}
