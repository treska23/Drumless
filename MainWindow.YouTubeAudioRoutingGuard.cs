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

        // El comprobador antiguo añadía la captura al mezclador con WebView2 todavía audible.
        // Eso producía dos rutas simultáneas y, después, podía validar por error audio almacenado
        // en el búfer antes de dejar el vídeo completamente mudo. Se mantiene bloqueado durante
        // toda la vida de la ventana y este guardián realiza la transición en orden seguro.
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
        }
        catch (ObjectDisposedException)
        {
        }

        _youtubeAudioRoutingGuardCore = null;
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
        _ = RestoreDirectYouTubeAudioAsync(
            "Cambiando de vídeo; se reiniciará la ruta de audio.");
    }

    private void OnYouTubeAudioRoutingGuardProcessFailed(
        object? sender,
        CoreWebView2ProcessFailedEventArgs eventArgs)
    {
        Interlocked.Increment(ref _youtubeSafeRoutingVersion);
        Interlocked.Increment(ref _managedYouTubeAudioRecoveryVersion);
        _ = RestoreDirectYouTubeAudioAsync(
            "La captura de YouTube se detuvo; se restauró la salida normal del navegador.");
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
        await RestoreDirectYouTubeAudioAsync(
            "Reconectando YouTube con la nueva salida de audio…");
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
                !string.Equals(
                    typeElement.GetString(),
                    "video-state",
                    StringComparison.Ordinal) ||
                !root.TryGetProperty("playing", out var playingElement) ||
                playingElement.ValueKind is not JsonValueKind.True)
            {
                return;
            }

            // Invalida los reintentos antiguos que todavía podrían despertar después de su delay.
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
            // Elimina cualquier captura anterior antes de tocar el mute del navegador.
            await _viewModel.ResetYouTubeAudioRoutingAsync();
            if (!IsSafeYouTubeRouteCurrent(routeVersion, core))
            {
                return;
            }

            // Punto clave: primero se silencia la salida directa y sólo después se añade la captura
            // al mezclador. Nunca pueden oírse al mismo tiempo WebView2 y la ruta de Drumless.
            core.IsMuted = true;
            stage = "crear la captura silenciada";
            await _viewModel.StartYouTubeAudioRoutingAsync(core.BrowserProcessId);
            if (!IsSafeYouTubeRouteCurrent(routeVersion, core))
            {
                await RestoreDirectYouTubeAudioAsync();
                return;
            }

            _viewModel.TakeYouTubeAudioPeak();
            var activeSamples = 0;
            var consecutiveActiveSamples = 0;
            stage = "comprobar una señal estable";
            for (var sample = 0; sample < 8; sample++)
            {
                await Task.Delay(150);
                if (!IsSafeYouTubeRouteCurrent(routeVersion, core))
                {
                    await RestoreDirectYouTubeAudioAsync();
                    return;
                }

                var peak = _viewModel.TakeYouTubeAudioPeak();
                if (peak >= YouTubeAudioSignalThreshold)
                {
                    activeSamples++;
                    consecutiveActiveSamples++;
                }
                else
                {
                    consecutiveActiveSamples = 0;
                }

                // Dos ventanas consecutivas y tres en total impiden aceptar restos breves o un
                // paquete aislado. La captura tiene que seguir viva después de aplicar el mute.
                if (activeSamples >= 3 && consecutiveActiveSamples >= 2)
                {
                    _viewModel.ConfirmYouTubeAudioRouting();
                    YouTubeStatusText.Text =
                        "Reproduciendo · audio enviado una sola vez a la salida elegida";
                    return;
                }
            }

            await RestoreDirectYouTubeAudioAsync(
                "La captura quedó sin señal; se restauró el sonido normal de YouTube.");
            YouTubeStatusText.Text =
                "YouTube se mantiene en la salida normal porque la captura no conservó audio";
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            System.Runtime.InteropServices.COMException or
            NotSupportedException or
            TimeoutException)
        {
            await RestoreDirectYouTubeAudioAsync(
                $"No se pudo enrutar YouTube al {stage}: {exception.Message}");
            YouTubeStatusText.Text =
                "No se pudo usar la captura; se restauró el sonido normal de YouTube";
        }
        finally
        {
            Volatile.Write(ref _youtubeSafeRoutingInProgress, 0);
        }
    }

    private bool IsSafeYouTubeRouteCurrent(long version, CoreWebView2 core) =>
        version == Volatile.Read(ref _youtubeSafeRoutingVersion) &&
        ReferenceEquals(core, YouTubeWebView.CoreWebView2);

    private async Task RestoreDirectYouTubeAudioAsync(string? reason = null)
    {
        try
        {
            await _viewModel.ResetYouTubeAudioRoutingAsync();
        }
        catch (ObjectDisposedException)
        {
        }

        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            try
            {
                core.IsMuted = false;
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            _viewModel.StopYouTubeAudioRouting(reason);
        }
    }
}
