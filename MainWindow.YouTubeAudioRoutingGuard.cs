using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using DrumPracticeStudio.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace DrumPracticeStudio;

internal static class YouTubeAudioRoutingGuardBootstrapper
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
            window.AttachYouTubeAudioRoutingGuard();
        }
    }
}

public partial class MainWindow
{
    private const float YouTubeAudioSignalThreshold = 0.0005f;
    private const int YouTubeAudioRoutingAttempts = 2;
    private const int YouTubeAudioValidationSamples = 10;

    private bool _youtubeAudioRoutingGuardAttached;
    private CoreWebView2? _youtubeAudioRoutingGuardCore;
    private long _youtubeSafeRoutingVersion;
    private int _youtubeSafeRoutingInProgress;

    internal void AttachYouTubeAudioRoutingGuard()
    {
        if (_youtubeAudioRoutingGuardAttached)
        {
            return;
        }

        _youtubeAudioRoutingGuardAttached = true;

        // Se bloquea el comprobador anterior porque podía desmutear WebView2 y enviar el sonido
        // al dispositivo predeterminado de Windows. YouTube sólo puede salir por Drumless.
        Volatile.Write(ref _youtubeAudioRoutingInProgress, 1);
        Interlocked.Increment(ref _youtubeAudioProbeVersion);
        Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);

        YouTubeWebView.CoreWebView2InitializationCompleted +=
            OnYouTubeAudioRoutingGuardInitializationCompleted;
        YouTubeWebView.NavigationStarting += OnYouTubeAudioRoutingGuardNavigationStarting;
        _viewModel.PropertyChanged += OnYouTubeAudioRoutingGuardViewModelPropertyChanged;
        Closed += OnYouTubeAudioRoutingGuardClosed;
        AttachYouTubeAudioRoutingGuardCore();
    }

    private void OnYouTubeAudioRoutingGuardInitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess)
        {
            AttachYouTubeAudioRoutingGuardCore();
        }
    }

    private void AttachYouTubeAudioRoutingGuardCore()
    {
        if (YouTubeWebView.CoreWebView2 is not { } core ||
            ReferenceEquals(core, _youtubeAudioRoutingGuardCore))
        {
            return;
        }

        DetachYouTubeAudioRoutingGuardCore();
        _youtubeAudioRoutingGuardCore = core;
        core.WebMessageReceived += OnYouTubeAudioRoutingGuardWebMessageReceived;
        core.ProcessFailed += OnYouTubeAudioRoutingGuardProcessFailed;
        core.IsMutedChanged += OnYouTubeAudioRoutingGuardMutedChanged;
        EnforceSelectedOutputOnly(core);
    }

    private void DetachYouTubeAudioRoutingGuardCore()
    {
        if (_youtubeAudioRoutingGuardCore is not { } core)
        {
            return;
        }

        try
        {
            core.WebMessageReceived -= OnYouTubeAudioRoutingGuardWebMessageReceived;
            core.ProcessFailed -= OnYouTubeAudioRoutingGuardProcessFailed;
            core.IsMutedChanged -= OnYouTubeAudioRoutingGuardMutedChanged;
        }
        catch (InvalidOperationException)
        {
        }

        _youtubeAudioRoutingGuardCore = null;
    }

    private void OnYouTubeAudioRoutingGuardMutedChanged(object? sender, object eventArgs)
    {
        if (sender is CoreWebView2 core)
        {
            EnforceSelectedOutputOnly(core);
        }
    }

    private static void EnforceSelectedOutputOnly(CoreWebView2 core)
    {
        try
        {
            if (!core.IsMuted)
            {
                core.IsMuted = true;
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnYouTubeAudioRoutingGuardClosed(object? sender, EventArgs eventArgs)
    {
        Interlocked.Increment(ref _youtubeSafeRoutingVersion);
        DetachYouTubeAudioRoutingGuardCore();
        YouTubeWebView.CoreWebView2InitializationCompleted -=
            OnYouTubeAudioRoutingGuardInitializationCompleted;
        YouTubeWebView.NavigationStarting -= OnYouTubeAudioRoutingGuardNavigationStarting;
        _viewModel.PropertyChanged -= OnYouTubeAudioRoutingGuardViewModelPropertyChanged;
        Closed -= OnYouTubeAudioRoutingGuardClosed;
    }

    private void OnYouTubeAudioRoutingGuardNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        Interlocked.Increment(ref _youtubeSafeRoutingVersion);
        Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);
        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            EnforceSelectedOutputOnly(core);
        }

        _ = ResetSelectedYouTubeOutputAsync(
            "Cambiando de vídeo; reconstruyendo la ruta de audio de Drumless…");
    }

    private void OnYouTubeAudioRoutingGuardProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs eventArgs)
    {
        Interlocked.Increment(ref _youtubeSafeRoutingVersion);
        Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);
        _ = FailSelectedYouTubeOutputAsync(
            "El proceso de audio de YouTube se detuvo.");
    }

    private void OnYouTubeAudioRoutingGuardViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(MainViewModel.AudioOutputStatus) ||
            YouTubeWebView.CoreWebView2 is not { IsDocumentPlayingAudio: true })
        {
            return;
        }

        Interlocked.Increment(ref _youtubeSafeRoutingVersion);
        Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);
        _ = RestartSafeYouTubeAudioRoutingAsync();
    }

    private async Task RestartSafeYouTubeAudioRoutingAsync()
    {
        await ResetSelectedYouTubeOutputAsync(
            "Reconectando YouTube con la nueva salida elegida en Drumless…");
        await Task.Delay(120);
        await EnsureYouTubeAudioRoutingWithoutDuplicationAsync();
    }

    private void OnYouTubeAudioRoutingGuardWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using var document = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeElement) ||
                !string.Equals(typeElement.GetString(), "video-state", StringComparison.Ordinal) ||
                !root.TryGetProperty("playing", out var playingElement) ||
                playingElement.ValueKind is not JsonValueKind.True)
            {
                return;
            }

            Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);
            _ = EnsureYouTubeAudioRoutingWithoutDuplicationAsync();
        }
        catch (JsonException)
        {
        }
    }

    private async Task EnsureYouTubeAudioRoutingWithoutDuplicationAsync()
    {
        var core = YouTubeWebView.CoreWebView2;
        if (core is null ||
            _viewModel.IsYouTubeAudioRouted ||
            Interlocked.CompareExchange(ref _youtubeSafeRoutingInProgress, 1, 0) != 0)
        {
            return;
        }

        var routeVersion = Interlocked.Increment(ref _youtubeSafeRoutingVersion);
        Interlocked.Increment(ref _youtubeAudioProbeVersion);
        Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);
        var stage = "preparar la captura";

        try
        {
            EnforceSelectedOutputOnly(core);

            for (var attempt = 1; attempt <= YouTubeAudioRoutingAttempts; attempt++)
            {
                await _viewModel.ResetYouTubeAudioRoutingAsync(
                    $"Conectando YouTube con la salida elegida · intento {attempt}/{YouTubeAudioRoutingAttempts}…");
                if (!IsSafeYouTubeRouteCurrent(routeVersion, core))
                {
                    return;
                }

                EnforceSelectedOutputOnly(core);
                stage = "crear la captura para la salida elegida";
                await _viewModel.StartYouTubeAudioRoutingAsync(core.BrowserProcessId);
                if (!IsSafeYouTubeRouteCurrent(routeVersion, core))
                {
                    await ResetSelectedYouTubeOutputAsync();
                    return;
                }

                _viewModel.TakeYouTubeAudioPeak();
                var activeSamples = 0;
                var consecutiveActiveSamples = 0;
                stage = "comprobar una señal estable";

                for (var sample = 0; sample < YouTubeAudioValidationSamples; sample++)
                {
                    await Task.Delay(150);
                    if (!IsSafeYouTubeRouteCurrent(routeVersion, core))
                    {
                        await ResetSelectedYouTubeOutputAsync();
                        return;
                    }

                    EnforceSelectedOutputOnly(core);
                    if (_viewModel.TakeYouTubeAudioPeak() >= YouTubeAudioSignalThreshold)
                    {
                        activeSamples++;
                        consecutiveActiveSamples++;
                    }
                    else
                    {
                        consecutiveActiveSamples = 0;
                    }

                    if (activeSamples >= 3 && consecutiveActiveSamples >= 2)
                    {
                        _viewModel.ConfirmYouTubeAudioRouting();
                        YouTubeStatusText.Text =
                            "Reproduciendo · YouTube sale únicamente por el dispositivo elegido en Drumless";
                        return;
                    }
                }

                await _viewModel.ResetYouTubeAudioRoutingAsync(
                    "La captura no mantuvo señal; reintentando sin usar la salida general de Windows…");
                if (attempt < YouTubeAudioRoutingAttempts)
                {
                    await Task.Delay(220);
                }
            }

            await FailSelectedYouTubeOutputAsync(
                "No se pudo conectar este vídeo con la salida elegida en Drumless.");
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            System.Runtime.InteropServices.COMException or
            NotSupportedException or
            TimeoutException)
        {
            await FailSelectedYouTubeOutputAsync(
                $"No se pudo enrutar YouTube al {stage}: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _youtubeSafeRoutingInProgress, 0);
        }
    }

    private bool IsSafeYouTubeRouteCurrent(long version, CoreWebView2 core) =>
        version == Volatile.Read(ref _youtubeSafeRoutingVersion) &&
        ReferenceEquals(core, YouTubeWebView.CoreWebView2);

    private async Task ResetSelectedYouTubeOutputAsync(string? reason = null)
    {
        try
        {
            await _viewModel.ResetYouTubeAudioRoutingAsync(reason);
        }
        catch (InvalidOperationException)
        {
        }

        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            EnforceSelectedOutputOnly(core);
        }
    }

    private async Task FailSelectedYouTubeOutputAsync(string reason)
    {
        await ResetSelectedYouTubeOutputAsync(reason);
        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            EnforceSelectedOutputOnly(core);
            try
            {
                await core.ExecuteScriptAsync(
                    "(() => { const video=document.querySelector('video'); if(video) video.pause(); })();");
            }
            catch (InvalidOperationException)
            {
            }
        }

        _viewModel.SetYouTubeTransportPlaying(false);
        YouTubeStatusText.Text =
            "YouTube pausado · no se enviará audio a la tele ni a la salida general del PC";
    }
}
