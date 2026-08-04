using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using DrumPracticeStudio.Services;
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
    private const int YouTubeAudioValidationSamples = 12;

    private readonly SemaphoreSlim _youtubeSessionMuteGuardGate = new(1, 1);
    private bool _youtubeAudioRoutingGuardAttached;
    private bool _youtubeCoreRenderEnabled;
    private CoreWebView2? _youtubeAudioRoutingGuardCore;
    private ProcessAudioSessionMuteGuard? _youtubeSessionMuteGuard;
    private long _youtubeSafeRoutingVersion;
    private int _youtubeSafeRoutingInProgress;

    internal void AttachYouTubeAudioRoutingGuard()
    {
        if (_youtubeAudioRoutingGuardAttached)
        {
            return;
        }

        _youtubeAudioRoutingGuardAttached = true;

        // El comprobador antiguo silenciaba WebView2 antes de capturarlo. Eso termina cortando el
        // propio flujo de render y sólo deja en el búfer unos instantes de audio. Esta versión
        // mantiene bloqueado aquel comprobador y realiza el mute en la sesión de Windows, después
        // del punto del que se alimenta la captura por proceso.
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
        SetCoreRenderEnabled(core, enabled: false);
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
            ApplyCoreRenderState(core);
        }
    }

    private void SetCoreRenderEnabled(CoreWebView2 core, bool enabled)
    {
        _youtubeCoreRenderEnabled = enabled;
        ApplyCoreRenderState(core);
    }

    private void ApplyCoreRenderState(CoreWebView2 core)
    {
        try
        {
            var shouldBeMuted = !_youtubeCoreRenderEnabled;
            if (core.IsMuted != shouldBeMuted)
            {
                core.IsMuted = shouldBeMuted;
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async void OnYouTubeAudioRoutingGuardClosed(object? sender, EventArgs eventArgs)
    {
        Interlocked.Increment(ref _youtubeSafeRoutingVersion);
        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            SetCoreRenderEnabled(core, enabled: false);
        }

        DetachYouTubeAudioRoutingGuardCore();
        YouTubeWebView.CoreWebView2InitializationCompleted -=
            OnYouTubeAudioRoutingGuardInitializationCompleted;
        YouTubeWebView.NavigationStarting -= OnYouTubeAudioRoutingGuardNavigationStarting;
        _viewModel.PropertyChanged -= OnYouTubeAudioRoutingGuardViewModelPropertyChanged;
        Closed -= OnYouTubeAudioRoutingGuardClosed;

        try
        {
            await _viewModel.ResetYouTubeAudioRoutingAsync();
        }
        catch (InvalidOperationException)
        {
        }

        await DisposeYouTubeSessionMuteGuardAsync();
        _youtubeSessionMuteGuardGate.Dispose();
    }

    private void OnYouTubeAudioRoutingGuardNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        Interlocked.Increment(ref _youtubeSafeRoutingVersion);
        Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);
        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            SetCoreRenderEnabled(core, enabled: false);
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
        var stage = "preparar la sesión de audio";

        try
        {
            SetCoreRenderEnabled(core, enabled: false);
            var sessionGuard = await EnsureYouTubeSessionMuteGuardAsync(core.BrowserProcessId);
            var sessionProtected = await sessionGuard.WaitUntilProtectedAsync(
                TimeSpan.FromSeconds(3));
            if (!sessionProtected || !IsSafeYouTubeRouteCurrent(routeVersion, core))
            {
                await FailSelectedYouTubeOutputAsync(
                    "Windows no creó una sesión de audio protegible para WebView2.");
                return;
            }

            for (var attempt = 1; attempt <= YouTubeAudioRoutingAttempts; attempt++)
            {
                SetCoreRenderEnabled(core, enabled: false);
                await _viewModel.ResetYouTubeAudioRoutingAsync(
                    $"Conectando YouTube con la salida elegida · intento {attempt}/{YouTubeAudioRoutingAttempts}…");
                if (!IsSafeYouTubeRouteCurrent(routeVersion, core))
                {
                    return;
                }

                // Con WebView2 todavía bloqueado a nivel global se crea primero la captura. Después
                // se reactiva el render del navegador: su sesión de Windows ya está silenciada, de
                // modo que el único sonido audible es el que pasa por el mezclador de Drumless.
                stage = "crear la captura por proceso";
                await _viewModel.StartYouTubeAudioRoutingAsync(core.BrowserProcessId);
                if (!IsSafeYouTubeRouteCurrent(routeVersion, core))
                {
                    await ResetSelectedYouTubeOutputAsync();
                    return;
                }

                _viewModel.TakeYouTubeAudioPeak();
                SetCoreRenderEnabled(core, enabled: true);
                var activeSamples = 0;
                var consecutiveActiveSamples = 0;
                stage = "comprobar el flujo después del mute de sesión";

                for (var sample = 0; sample < YouTubeAudioValidationSamples; sample++)
                {
                    await Task.Delay(120);
                    if (!IsSafeYouTubeRouteCurrent(routeVersion, core))
                    {
                        await ResetSelectedYouTubeOutputAsync();
                        return;
                    }

                    ApplyCoreRenderState(core);
                    if (_viewModel.TakeYouTubeAudioPeak() >= YouTubeAudioSignalThreshold)
                    {
                        activeSamples++;
                        consecutiveActiveSamples++;
                    }
                    else
                    {
                        consecutiveActiveSamples = 0;
                    }

                    if (activeSamples >= 4 && consecutiveActiveSamples >= 3)
                    {
                        _viewModel.ConfirmYouTubeAudioRouting();
                        YouTubeStatusText.Text =
                            "Reproduciendo · WebView2 capturado antes del mute y enviado a la salida de Drumless";
                        return;
                    }
                }

                SetCoreRenderEnabled(core, enabled: false);
                await _viewModel.ResetYouTubeAudioRoutingAsync(
                    "La captura perdió el flujo; reintentando la conexión con Drumless…");
                if (attempt < YouTubeAudioRoutingAttempts)
                {
                    await Task.Delay(220);
                }
            }

            await FailSelectedYouTubeOutputAsync(
                "La captura por proceso no conservó audio después de proteger la salida directa.");
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

    private async Task<ProcessAudioSessionMuteGuard> EnsureYouTubeSessionMuteGuardAsync(
        uint browserProcessId)
    {
        await _youtubeSessionMuteGuardGate.WaitAsync();
        try
        {
            if (_youtubeSessionMuteGuard is { } current &&
                current.RootProcessId == browserProcessId)
            {
                return current;
            }

            if (_youtubeSessionMuteGuard is { } previous)
            {
                _youtubeSessionMuteGuard = null;
                await previous.DisposeAsync();
            }

            var replacement = ProcessAudioSessionMuteGuard.Start(browserProcessId);
            _youtubeSessionMuteGuard = replacement;
            return replacement;
        }
        finally
        {
            _youtubeSessionMuteGuardGate.Release();
        }
    }

    private async Task DisposeYouTubeSessionMuteGuardAsync()
    {
        await _youtubeSessionMuteGuardGate.WaitAsync();
        try
        {
            if (_youtubeSessionMuteGuard is not { } guard)
            {
                return;
            }

            _youtubeSessionMuteGuard = null;
            await guard.DisposeAsync();
        }
        finally
        {
            _youtubeSessionMuteGuardGate.Release();
        }
    }

    private bool IsSafeYouTubeRouteCurrent(long version, CoreWebView2 core) =>
        version == Volatile.Read(ref _youtubeSafeRoutingVersion) &&
        ReferenceEquals(core, YouTubeWebView.CoreWebView2);

    private async Task ResetSelectedYouTubeOutputAsync(string? reason = null)
    {
        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            SetCoreRenderEnabled(core, enabled: false);
        }

        try
        {
            await _viewModel.ResetYouTubeAudioRoutingAsync(reason);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private async Task FailSelectedYouTubeOutputAsync(string reason)
    {
        await ResetSelectedYouTubeOutputAsync(reason);
        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            SetCoreRenderEnabled(core, enabled: false);
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
            "YouTube pausado · la salida directa está protegida y no se ha desviado a Windows";
    }
}
