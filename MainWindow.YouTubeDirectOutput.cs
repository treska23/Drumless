using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using DrumPracticeStudio.Models;
using Microsoft.Web.WebView2.Core;

namespace DrumPracticeStudio;

internal static class YouTubeDirectOutputBootstrapper
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
            window.AttachYouTubeDirectOutputRouting();
        }
    }
}

public partial class MainWindow
{
    private const string YouTubeOrigin = "https://www.youtube.com";

    private bool _youtubeDirectOutputAttached;
    private bool _youtubeDirectOutputActive;
    private bool _youtubeDirectOutputClosing;
    private CoreWebView2? _youtubeDirectOutputCore;
    private int _youtubeDirectOutputInProgress;
    private long _youtubeDirectOutputGeneration;

    internal void AttachYouTubeDirectOutputRouting()
    {
        if (_youtubeDirectOutputAttached)
        {
            return;
        }

        _youtubeDirectOutputAttached = true;

        // Esta rama no usa captura loopback. El vídeo se conecta directamente al endpoint físico
        // correspondiente a la salida elegida en Drumless mediante HTMLMediaElement.setSinkId().
        // Mantener este indicador ocupado impide que la ruta antigua arranque otra copia del audio.
        Volatile.Write(ref _youtubeAudioRoutingInProgress, 1);

        YouTubeWebView.CoreWebView2InitializationCompleted +=
            OnYouTubeDirectOutputInitializationCompleted;
        YouTubeWebView.NavigationStarting += OnYouTubeDirectOutputNavigationStarting;
        YouTubeWebView.NavigationCompleted += OnYouTubeDirectOutputNavigationCompleted;
        _viewModel.PropertyChanged += OnYouTubeDirectOutputViewModelPropertyChanged;
        Closed += OnYouTubeDirectOutputClosed;
        AttachYouTubeDirectOutputCore();
    }

    private void OnYouTubeDirectOutputInitializationCompleted(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess)
        {
            AttachYouTubeDirectOutputCore();
        }
    }

    private void AttachYouTubeDirectOutputCore()
    {
        if (YouTubeWebView.CoreWebView2 is not { } core ||
            ReferenceEquals(core, _youtubeDirectOutputCore))
        {
            return;
        }

        DetachYouTubeDirectOutputCore();
        _youtubeDirectOutputCore = core;
        core.WebMessageReceived += OnYouTubeDirectOutputWebMessageReceived;
        core.IsMutedChanged += OnYouTubeDirectOutputMutedChanged;
        _youtubeDirectOutputActive = false;
        EnforceYouTubeDirectOutputMute(core);
    }

    private void DetachYouTubeDirectOutputCore()
    {
        if (_youtubeDirectOutputCore is not { } core)
        {
            return;
        }

        try
        {
            core.WebMessageReceived -= OnYouTubeDirectOutputWebMessageReceived;
            core.IsMutedChanged -= OnYouTubeDirectOutputMutedChanged;
        }
        catch (InvalidOperationException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _youtubeDirectOutputCore = null;
    }

    private void OnYouTubeDirectOutputNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        Interlocked.Increment(ref _youtubeDirectOutputGeneration);
        _youtubeDirectOutputActive = false;
        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            EnforceYouTubeDirectOutputMute(core);
        }
    }

    private async void OnYouTubeDirectOutputNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess || YouTubeWebView.CoreWebView2 is not { } core)
        {
            return;
        }

        AttachYouTubeDirectOutputCore();
        EnforceYouTubeDirectOutputMute(core);
        await InstallYouTubeDirectOutputObserverAsync(core);
    }

    private void OnYouTubeDirectOutputMutedChanged(object? sender, object eventArgs)
    {
        if (sender is CoreWebView2 core)
        {
            EnforceYouTubeDirectOutputMute(core);
        }
    }

    private void EnforceYouTubeDirectOutputMute(CoreWebView2 core)
    {
        if (_youtubeDirectOutputClosing)
        {
            return;
        }

        var shouldBeMuted = !_youtubeDirectOutputActive;
        try
        {
            if (core.IsMuted != shouldBeMuted)
            {
                core.IsMuted = shouldBeMuted;
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnYouTubeDirectOutputViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(ViewModels.MainViewModel.SelectedAudioOutputDevice))
        {
            return;
        }

        Interlocked.Increment(ref _youtubeDirectOutputGeneration);
        _youtubeDirectOutputActive = false;
        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            EnforceYouTubeDirectOutputMute(core);
            _ = RouteYouTubeDirectlyToSelectedOutputAsync();
        }
    }

    private async Task InstallYouTubeDirectOutputObserverAsync(CoreWebView2 core)
    {
        try
        {
            await core.ExecuteScriptAsync(
                """
                (() => {
                  if (window.__dpsDirectOutputObserverInstalled) return;
                  window.__dpsDirectOutputObserverInstalled = true;
                  const attach = () => {
                    const video = document.querySelector('video');
                    if (!video || video.__dpsDirectOutputAttached) return;
                    video.__dpsDirectOutputAttached = true;
                    video.addEventListener('play', () => {
                      chrome.webview.postMessage({ type: 'drumless-direct-output-request' });
                    });
                  };
                  attach();
                  new MutationObserver(attach).observe(
                    document.documentElement,
                    { childList: true, subtree: true });
                  const current = document.querySelector('video');
                  if (current && !current.paused && !current.ended) {
                    chrome.webview.postMessage({ type: 'drumless-direct-output-request' });
                  }
                })();
                """);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnYouTubeDirectOutputWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        string? type;
        bool ok;
        string? label;
        string? reason;
        string[]? available;
        try
        {
            using var document = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            var root = document.RootElement;
            type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            ok = root.TryGetProperty("ok", out var okElement) &&
                 okElement.ValueKind is JsonValueKind.True;
            label = root.TryGetProperty("label", out var labelElement)
                ? labelElement.GetString()
                : null;
            reason = root.TryGetProperty("reason", out var reasonElement)
                ? reasonElement.GetString()
                : null;
            available = root.TryGetProperty("available", out var availableElement) &&
                        availableElement.ValueKind is JsonValueKind.Array
                ? availableElement
                    .EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Cast<string>()
                    .ToArray()
                : null;
        }
        catch (JsonException)
        {
            return;
        }

        if (string.Equals(
                type,
                "drumless-direct-output-request",
                StringComparison.Ordinal))
        {
            _ = RouteYouTubeDirectlyToSelectedOutputAsync();
            return;
        }

        if (!string.Equals(
                type,
                "drumless-direct-output-result",
                StringComparison.Ordinal))
        {
            return;
        }

        if (ok && YouTubeWebView.CoreWebView2 is { } core)
        {
            _youtubeDirectOutputActive = true;
            EnforceYouTubeDirectOutputMute(core);
            YouTubeStatusText.Text =
                $"Reproduciendo · YouTube conectado directamente a {label ?? "la salida elegida"}";
            return;
        }

        _youtubeDirectOutputActive = false;
        if (YouTubeWebView.CoreWebView2 is { } failedCore)
        {
            EnforceYouTubeDirectOutputMute(failedCore);
        }

        var availableText = available is { Length: > 0 }
            ? $" Salidas visibles: {string.Join(" | ", available)}."
            : string.Empty;
        YouTubeStatusText.Text =
            $"YouTube pausado · no se pudo usar la salida elegida ({reason ?? "sin coincidencia"})." +
            availableText;
    }

    private async Task RouteYouTubeDirectlyToSelectedOutputAsync()
    {
        var core = YouTubeWebView.CoreWebView2;
        var selected = _viewModel.SelectedAudioOutputDevice;
        if (core is null || selected is null ||
            Interlocked.CompareExchange(ref _youtubeDirectOutputInProgress, 1, 0) != 0)
        {
            return;
        }

        var generation = Volatile.Read(ref _youtubeDirectOutputGeneration);
        _youtubeDirectOutputActive = false;
        EnforceYouTubeDirectOutputMute(core);

        try
        {
            var aliases = BuildYouTubeOutputAliases(selected);
            var aliasesJson = JsonSerializer.Serialize(aliases);

            // Chromium oculta normalmente las etiquetas de las salidas hasta que el origen obtiene
            // permiso de audio. Se concede sólo durante esta operación y se vuelve a Default al salir.
            await core.Profile.SetPermissionStateAsync(
                CoreWebView2PermissionKind.Microphone,
                YouTubeOrigin,
                CoreWebView2PermissionState.Allow);

            if (generation != Volatile.Read(ref _youtubeDirectOutputGeneration))
            {
                return;
            }

            await core.ExecuteScriptAsync(
                $$"""
                (async () => {
                  const aliases = {{aliasesJson}};
                  const post = payload => chrome.webview.postMessage({
                    type: 'drumless-direct-output-result',
                    ...payload
                  });
                  const normalize = value => String(value || '')
                    .toLocaleLowerCase()
                    .normalize('NFD')
                    .replace(/[\u0300-\u036f]/g, '')
                    .replace(/[^a-z0-9]+/g, ' ')
                    .trim();
                  const ignored = new Set([
                    'audio', 'asio', 'directo', 'direct', 'driver', 'output', 'salida',
                    'speaker', 'speakers', 'altavoz', 'altavoces', 'headphone',
                    'headphones', 'auriculares', 'usb', 'wasapi', 'device'
                  ]);
                  const tokens = value => normalize(value)
                    .split(/\s+/)
                    .filter(token => token.length > 1 && !ignored.has(token));
                  const score = (label, alias) => {
                    const left = normalize(label);
                    const right = normalize(alias);
                    if (!left || !right) return 0;
                    if (left === right) return 1000;
                    if (left.includes(right) || right.includes(left)) return 700;
                    const rightTokens = tokens(right);
                    if (!rightTokens.length) return 0;
                    const leftTokens = new Set(tokens(left));
                    const shared = rightTokens.filter(token => leftTokens.has(token)).length;
                    return shared * 100 / rightTokens.length;
                  };

                  try {
                    const video = document.querySelector('video');
                    if (!video) {
                      post({ ok: false, reason: 'YouTube todavía no ha creado el vídeo' });
                      return;
                    }
                    if (typeof video.setSinkId !== 'function' || !navigator.mediaDevices) {
                      video.pause();
                      post({ ok: false, reason: 'WebView2 no ofrece setSinkId' });
                      return;
                    }

                    let devices = await navigator.mediaDevices.enumerateDevices();
                    let outputs = devices.filter(device => device.kind === 'audiooutput');
                    if (!outputs.some(device => device.label)) {
                      const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
                      for (const track of stream.getTracks()) track.stop();
                      devices = await navigator.mediaDevices.enumerateDevices();
                      outputs = devices.filter(device => device.kind === 'audiooutput');
                    }

                    const ranked = outputs
                      .map(device => ({
                        device,
                        score: Math.max(...aliases.map(alias => score(device.label, alias)), 0)
                      }))
                      .sort((left, right) => right.score - left.score);
                    const best = ranked[0];
                    const available = outputs.map(device => device.label || '(sin nombre)');
                    if (!best || best.score < 45 || !best.device.deviceId) {
                      video.pause();
                      post({
                        ok: false,
                        reason: 'ninguna salida de WebView2 coincide con la elegida en Drumless',
                        available
                      });
                      return;
                    }

                    await video.setSinkId(best.device.deviceId);
                    post({
                      ok: true,
                      label: best.device.label,
                      available
                    });
                  } catch (error) {
                    try { document.querySelector('video')?.pause(); } catch (_) {}
                    post({
                      ok: false,
                      reason: String(error?.name || '') + ': ' + String(error?.message || error || '')
                    });
                  }
                })();
                """);
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or
            NotSupportedException or
            System.Runtime.InteropServices.COMException)
        {
            _youtubeDirectOutputActive = false;
            EnforceYouTubeDirectOutputMute(core);
            YouTubeStatusText.Text =
                $"YouTube pausado · no se pudo preparar la salida directa: {exception.Message}";
            try
            {
                await core.ExecuteScriptAsync(
                    "document.querySelector('video')?.pause();");
            }
            catch (InvalidOperationException)
            {
            }
        }
        finally
        {
            try
            {
                await core.Profile.SetPermissionStateAsync(
                    CoreWebView2PermissionKind.Microphone,
                    YouTubeOrigin,
                    CoreWebView2PermissionState.Default);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.Runtime.InteropServices.COMException)
            {
            }

            Volatile.Write(ref _youtubeDirectOutputInProgress, 0);
        }
    }

    private string[] BuildYouTubeOutputAliases(AudioOutputDeviceItem selected)
    {
        var aliases = new List<string> { selected.Name };
        if (selected.IsAsio)
        {
            aliases.AddRange(
                _viewModel.AudioOutputDevices
                    .Where(device => !device.IsAsio)
                    .Select(device => new
                    {
                        device.Name,
                        Score = ScoreYouTubeOutputAlias(selected.Name, device.Name)
                    })
                    .Where(candidate => candidate.Score > 0)
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Take(4)
                    .Select(candidate => candidate.Name));
        }

        return aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static int ScoreYouTubeOutputAlias(string selected, string candidate)
    {
        var selectedTokens = TokenizeYouTubeOutputName(selected);
        var candidateTokens = TokenizeYouTubeOutputName(candidate);
        if (selectedTokens.Count == 0 || candidateTokens.Count == 0)
        {
            return 0;
        }

        var shared = selectedTokens.Intersect(candidateTokens).Count();
        return shared == 0
            ? 0
            : shared * 100 / Math.Max(selectedTokens.Count, candidateTokens.Count);
    }

    private static HashSet<string> TokenizeYouTubeOutputName(string value)
    {
        string[] ignored =
        [
            "audio", "asio", "directo", "direct", "driver", "output", "salida",
            "speaker", "speakers", "altavoz", "altavoces", "headphone", "headphones",
            "auriculares", "usb", "wasapi", "device"
        ];
        return value
            .ToLowerInvariant()
            .Split(
                [' ', '-', '_', '(', ')', '[', ']', '.', ',', ':'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1 && !ignored.Contains(token, StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    private async void OnYouTubeDirectOutputClosed(object? sender, EventArgs eventArgs)
    {
        _youtubeDirectOutputClosing = true;
        Interlocked.Increment(ref _youtubeDirectOutputGeneration);
        Volatile.Write(ref _youtubeAudioRoutingInProgress, 0);
        Volatile.Write(ref _youtubeDirectOutputInProgress, 0);

        YouTubeWebView.CoreWebView2InitializationCompleted -=
            OnYouTubeDirectOutputInitializationCompleted;
        YouTubeWebView.NavigationStarting -= OnYouTubeDirectOutputNavigationStarting;
        YouTubeWebView.NavigationCompleted -= OnYouTubeDirectOutputNavigationCompleted;
        _viewModel.PropertyChanged -= OnYouTubeDirectOutputViewModelPropertyChanged;
        Closed -= OnYouTubeDirectOutputClosed;
        DetachYouTubeDirectOutputCore();

        try
        {
            if (YouTubeWebView.CoreWebView2 is { } core)
            {
                await core.Profile.SetPermissionStateAsync(
                    CoreWebView2PermissionKind.Microphone,
                    YouTubeOrigin,
                    CoreWebView2PermissionState.Default);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.Runtime.InteropServices.COMException)
        {
        }
    }
}
