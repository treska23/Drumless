using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace DrumPracticeStudio;

internal static class YouTubeDirectOutputBootstrapper
{
    [ModuleInitializer]
    internal static void Initialize() =>
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                if (sender is MainWindow window)
                {
                    window.AttachYouTubeDirectOutputRouting();
                }
            }),
            handledEventsToo: true);
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

        // Impide que el enrutado antiguo cree una segunda copia mediante captura loopback.
        Volatile.Write(ref _youtubeAudioRoutingInProgress, 1);
        YouTubeWebView.CoreWebView2InitializationCompleted += OnDirectOutputInitialized;
        YouTubeWebView.NavigationStarting += OnDirectOutputNavigationStarting;
        YouTubeWebView.NavigationCompleted += OnDirectOutputNavigationCompleted;
        _viewModel.PropertyChanged += OnDirectOutputViewModelPropertyChanged;
        Closed += OnDirectOutputClosed;
        AttachDirectOutputCore();
    }

    private void OnDirectOutputInitialized(
        object? sender,
        CoreWebView2InitializationCompletedEventArgs eventArgs)
    {
        if (eventArgs.IsSuccess)
        {
            AttachDirectOutputCore();
        }
    }

    private void AttachDirectOutputCore()
    {
        if (YouTubeWebView.CoreWebView2 is not { } core ||
            ReferenceEquals(core, _youtubeDirectOutputCore))
        {
            return;
        }

        DetachDirectOutputCore();
        _youtubeDirectOutputCore = core;
        core.WebMessageReceived += OnDirectOutputWebMessageReceived;
        core.IsMutedChanged += OnDirectOutputMutedChanged;
        _youtubeDirectOutputActive = false;
        EnforceDirectOutputMute(core);
    }

    private void DetachDirectOutputCore()
    {
        if (_youtubeDirectOutputCore is not { } core)
        {
            return;
        }

        try
        {
            core.WebMessageReceived -= OnDirectOutputWebMessageReceived;
            core.IsMutedChanged -= OnDirectOutputMutedChanged;
        }
        catch (InvalidOperationException)
        {
        }
        _youtubeDirectOutputCore = null;
    }

    private void OnDirectOutputNavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs eventArgs)
    {
        Interlocked.Increment(ref _youtubeDirectOutputGeneration);
        _youtubeDirectOutputActive = false;
        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            EnforceDirectOutputMute(core);
        }
    }

    private async void OnDirectOutputNavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs eventArgs)
    {
        if (!eventArgs.IsSuccess || YouTubeWebView.CoreWebView2 is not { } core)
        {
            return;
        }

        AttachDirectOutputCore();
        EnforceDirectOutputMute(core);
        try
        {
            await core.ExecuteScriptAsync(
                """
                (() => {
                  if (window.__dpsDirectOutputObserver) return;
                  window.__dpsDirectOutputObserver = true;
                  const attach = () => {
                    const video = document.querySelector('video');
                    if (!video || video.__dpsDirectOutputAttached) return;
                    video.__dpsDirectOutputAttached = true;
                    video.addEventListener('play', () =>
                      chrome.webview.postMessage({ type: 'drumless-direct-output-request' }));
                    if (!video.paused && !video.ended) {
                      chrome.webview.postMessage({ type: 'drumless-direct-output-request' });
                    }
                  };
                  attach();
                  new MutationObserver(attach).observe(
                    document.documentElement,
                    { childList: true, subtree: true });
                })();
                """);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnDirectOutputMutedChanged(object? sender, object eventArgs)
    {
        if (sender is CoreWebView2 core)
        {
            EnforceDirectOutputMute(core);
        }
    }

    private void EnforceDirectOutputMute(CoreWebView2 core)
    {
        if (_youtubeDirectOutputClosing)
        {
            return;
        }

        try
        {
            var shouldBeMuted = !_youtubeDirectOutputActive;
            if (core.IsMuted != shouldBeMuted)
            {
                core.IsMuted = shouldBeMuted;
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnDirectOutputViewModelPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName != nameof(MainViewModel.SelectedAudioOutputDevice))
        {
            return;
        }

        Interlocked.Increment(ref _youtubeDirectOutputGeneration);
        _youtubeDirectOutputActive = false;
        if (YouTubeWebView.CoreWebView2 is { } core)
        {
            EnforceDirectOutputMute(core);
            _ = RouteYouTubeToSelectedOutputAsync();
        }
    }

    private void OnDirectOutputWebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs eventArgs)
    {
        try
        {
            using var document = JsonDocument.Parse(eventArgs.WebMessageAsJson);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;
            if (type == "drumless-direct-output-request")
            {
                _ = RouteYouTubeToSelectedOutputAsync();
                return;
            }
            if (type != "drumless-direct-output-result")
            {
                return;
            }

            var ok = root.TryGetProperty("ok", out var okElement) &&
                     okElement.ValueKind is JsonValueKind.True;
            if (ok && YouTubeWebView.CoreWebView2 is { } core)
            {
                var label = root.TryGetProperty("label", out var labelElement)
                    ? labelElement.GetString()
                    : null;
                _youtubeDirectOutputActive = true;
                EnforceDirectOutputMute(core);
                YouTubeStatusText.Text =
                    $"Reproduciendo · YouTube conectado directamente a {label ?? "la salida elegida"}";
                return;
            }

            _youtubeDirectOutputActive = false;
            if (YouTubeWebView.CoreWebView2 is { } failedCore)
            {
                EnforceDirectOutputMute(failedCore);
            }
            var reason = root.TryGetProperty("reason", out var reasonElement)
                ? reasonElement.GetString()
                : "sin coincidencia";
            var available = root.TryGetProperty("available", out var availableElement) &&
                            availableElement.ValueKind is JsonValueKind.Array
                ? availableElement.EnumerateArray()
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray()
                : [];
            YouTubeStatusText.Text =
                $"YouTube pausado · no se pudo usar la salida elegida ({reason})." +
                (available.Length > 0
                    ? $" Salidas visibles: {string.Join(" | ", available)}."
                    : string.Empty);
        }
        catch (JsonException)
        {
        }
    }

    private async Task RouteYouTubeToSelectedOutputAsync()
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
        EnforceDirectOutputMute(core);
        try
        {
            var aliasesJson = JsonSerializer.Serialize(BuildOutputAliases(selected));
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
                  const post = value => chrome.webview.postMessage({
                    type: 'drumless-direct-output-result', ...value
                  });
                  const normalize = value => String(value || '').toLowerCase()
                    .normalize('NFD').replace(/[\u0300-\u036f]/g, '')
                    .replace(/[^a-z0-9]+/g, ' ').trim();
                  const ignored = new Set(['audio','asio','directo','direct','driver','output',
                    'salida','speaker','speakers','altavoz','altavoces','headphone','headphones',
                    'auriculares','usb','wasapi','device']);
                  const tokens = value => normalize(value).split(/\s+/)
                    .filter(token => token.length > 1 && !ignored.has(token));
                  const score = (label, alias) => {
                    const left = normalize(label), right = normalize(alias);
                    if (!left || !right) return 0;
                    if (left === right) return 1000;
                    if (left.includes(right) || right.includes(left)) return 700;
                    const expected = tokens(right);
                    const actual = new Set(tokens(left));
                    return expected.length
                      ? expected.filter(token => actual.has(token)).length * 100 / expected.length
                      : 0;
                  };
                  try {
                    const video = document.querySelector('video');
                    if (!video) return post({ ok:false, reason:'YouTube aún no creó el vídeo' });
                    if (typeof video.setSinkId !== 'function' || !navigator.mediaDevices) {
                      video.pause();
                      return post({ ok:false, reason:'WebView2 no ofrece setSinkId' });
                    }
                    let devices = await navigator.mediaDevices.enumerateDevices();
                    let outputs = devices.filter(device => device.kind === 'audiooutput');
                    if (!outputs.some(device => device.label)) {
                      const stream = await navigator.mediaDevices.getUserMedia({ audio:true });
                      stream.getTracks().forEach(track => track.stop());
                      devices = await navigator.mediaDevices.enumerateDevices();
                      outputs = devices.filter(device => device.kind === 'audiooutput');
                    }
                    const ranked = outputs.map(device => ({ device, score:Math.max(
                      ...aliases.map(alias => score(device.label, alias)), 0) }))
                      .sort((a,b) => b.score-a.score);
                    const best = ranked[0];
                    const available = outputs.map(device => device.label || '(sin nombre)');
                    if (!best || best.score < 45 || !best.device.deviceId) {
                      video.pause();
                      return post({ ok:false,
                        reason:'ninguna salida coincide con la elegida en Drumless', available });
                    }
                    await video.setSinkId(best.device.deviceId);
                    post({ ok:true, label:best.device.label, available });
                  } catch (error) {
                    try { document.querySelector('video')?.pause(); } catch (_) {}
                    post({ ok:false,
                      reason:String(error?.name || '') + ': ' + String(error?.message || error || '') });
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
            EnforceDirectOutputMute(core);
            YouTubeStatusText.Text =
                $"YouTube pausado · no se pudo preparar la salida directa: {exception.Message}";
            try
            {
                await core.ExecuteScriptAsync("document.querySelector('video')?.pause();");
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
            catch (Exception exception) when (exception is
                InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
            }
            Volatile.Write(ref _youtubeDirectOutputInProgress, 0);
        }
    }

    private string[] BuildOutputAliases(AudioOutputDeviceItem selected)
    {
        var aliases = new List<string> { selected.Name };
        if (selected.IsAsio)
        {
            aliases.AddRange(_viewModel.AudioOutputDevices
                .Where(device => !device.IsAsio)
                .Select(device => (device.Name, Score: ScoreOutputName(selected.Name, device.Name)))
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .Take(4)
                .Select(candidate => candidate.Name));
        }
        return aliases.Distinct(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static int ScoreOutputName(string left, string right)
    {
        var first = TokenizeOutputName(left);
        var second = TokenizeOutputName(right);
        if (first.Count == 0 || second.Count == 0)
        {
            return 0;
        }
        return first.Intersect(second).Count() * 100 / Math.Max(first.Count, second.Count);
    }

    private static HashSet<string> TokenizeOutputName(string value)
    {
        string[] ignored = ["audio", "asio", "directo", "direct", "driver", "output",
            "salida", "speaker", "speakers", "altavoz", "altavoces", "headphone",
            "headphones", "auriculares", "usb", "wasapi", "device"];
        return value.ToLowerInvariant()
            .Split([' ', '-', '_', '(', ')', '[', ']', '.', ',', ':'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 1 && !ignored.Contains(token, StringComparer.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
    }

    private async void OnDirectOutputClosed(object? sender, EventArgs eventArgs)
    {
        _youtubeDirectOutputClosing = true;
        Interlocked.Increment(ref _youtubeDirectOutputGeneration);
        Volatile.Write(ref _youtubeAudioRoutingInProgress, 0);
        YouTubeWebView.CoreWebView2InitializationCompleted -= OnDirectOutputInitialized;
        YouTubeWebView.NavigationStarting -= OnDirectOutputNavigationStarting;
        YouTubeWebView.NavigationCompleted -= OnDirectOutputNavigationCompleted;
        _viewModel.PropertyChanged -= OnDirectOutputViewModelPropertyChanged;
        Closed -= OnDirectOutputClosed;
        DetachDirectOutputCore();
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
        catch (Exception exception) when (exception is
            InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
        }
    }
}
