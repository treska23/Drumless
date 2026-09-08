using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    private bool _youtubeRecordingBridgeAttached;

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
            UpdateYouTubeRecordingProcessId();
            return;
        }

        _youtubeRecordingBridgeAttached = true;
        YouTubeWebView.CoreWebView2InitializationCompleted +=
            OnYouTubeRecordingCoreInitializationCompleted;
        Closed += OnYouTubeRecordingBridgeClosed;
        UpdateYouTubeRecordingProcessId();
    }

    private void OnYouTubeRecordingCoreInitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess)
        {
            UpdateYouTubeRecordingProcessId();
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
        _viewModel.SetYouTubeBrowserProcessId(null);
    }
}
