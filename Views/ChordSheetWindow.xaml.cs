using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using DrumPracticeStudio.ViewModels;
using Microsoft.Web.WebView2.Core;

namespace DrumPracticeStudio.Views;

public partial class ChordSheetWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _isInitializing;

    public ChordSheetWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.CurrentChordSheetLineChanged += OnCurrentChordSheetLineChanged;
        _viewModel.ChordSheetSourceOpenRequested += OnChordSheetSourceOpenRequested;
        Closed += OnClosed;
        ContentRendered += async (_, _) =>
        {
            await EnsureWebViewReadyAsync();
            if (_viewModel.ChordSheetSourceCandidates.Count == 0)
            {
                await _viewModel.SearchChordSheetSourcesAsync();
            }
        };
        AddressBox.Text =
            $"https://duckduckgo.com/?q={Uri.EscapeDataString(viewModel.CurrentTrackTitle + " letra acordes")}";
    }

    private async void OnWebViewLoaded(object sender, RoutedEventArgs e) =>
        await EnsureWebViewReadyAsync();

    private async Task EnsureWebViewReadyAsync()
    {
        if (ChordWebView.CoreWebView2 is not null || _isInitializing)
        {
            return;
        }
        _isInitializing = true;
        try
        {
            Directory.CreateDirectory(AppPaths.ChordSheetWebViewData);
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: AppPaths.ChordSheetWebViewData);
            await ChordWebView.EnsureCoreWebView2Async(environment);
            var core = ChordWebView.CoreWebView2 ??
                       throw new InvalidOperationException("WebView2 no devolvió un navegador.");
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.NavigationStarting += (_, _) => BrowserStatus.Text = "Cargando…";
            core.NavigationCompleted += (_, args) =>
                BrowserStatus.Text = args.IsSuccess
                    ? "Selecciona el contenido deseado o pulsa extraer para detectar el bloque principal."
                    : $"No se pudo cargar la página ({args.WebErrorStatus}).";
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri))
                {
                    ChordWebView.Source = uri;
                    AddressBox.Text = uri.AbsoluteUri;
                }
            };
            Navigate();
        }
        catch (Exception exception)
        {
            BrowserStatus.Text = $"No se pudo iniciar el navegador: {exception.Message}";
        }
        finally
        {
            _isInitializing = false;
        }
    }

    private async void OnNavigateClick(object sender, RoutedEventArgs e)
    {
        await EnsureWebViewReadyAsync();
        Navigate();
    }

    private async void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }
        e.Handled = true;
        await EnsureWebViewReadyAsync();
        Navigate();
    }

    private void Navigate()
    {
        if (ChordWebView.CoreWebView2 is null)
        {
            return;
        }
        var value = AddressBox.Text.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            uri = new Uri(
                $"https://duckduckgo.com/?q={Uri.EscapeDataString(value)}");
            AddressBox.Text = uri.AbsoluteUri;
        }
        ChordWebView.Source = uri;
    }

    private async void OnExtractClick(object sender, RoutedEventArgs e) =>
        await ExtractCurrentPageAsync();

    private async Task ExtractCurrentPageAsync()
    {
        await EnsureWebViewReadyAsync();
        var core = ChordWebView.CoreWebView2;
        if (core is null)
        {
            return;
        }
        try
        {
            BrowserStatus.Text = "Leyendo la selección visible…";
            var result = await core.ExecuteScriptAsync(ExtractionScript);
            var payloadJson = JsonSerializer.Deserialize<string>(result);
            var payload = string.IsNullOrWhiteSpace(payloadJson)
                ? null
                : JsonSerializer.Deserialize<ExtractionPayload>(
                    payloadJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
            {
                BrowserStatus.Text =
                    "No se encontró contenido. Selecciona con el ratón la letra y los acordes y vuelve a intentarlo.";
                return;
            }
            _viewModel.SetChordSheetFromWeb(payload.Text, payload.Url, payload.Title);
            BrowserStatus.Text =
                $"Extraídas {payload.Text.Split('\n').Length} líneas · guardadas localmente.";
        }
        catch (Exception exception)
        {
            BrowserStatus.Text = $"No se pudo extraer el texto: {exception.Message}";
        }
    }

    private async void OnProcessCandidateClick(object sender, RoutedEventArgs e)
    {
        var candidate = _viewModel.SelectedChordSheetSource;
        if (candidate is null)
        {
            BrowserStatus.Text = "Selecciona primero una página candidata.";
            return;
        }
        await NavigateToCandidateAsync(candidate, extractAfterNavigation: true);
    }

    private async void OnChordSheetSourceOpenRequested(
        object? sender,
        ChordSheetSourceCandidate candidate) =>
        await NavigateToCandidateAsync(candidate, extractAfterNavigation: false);

    private async Task NavigateToCandidateAsync(
        ChordSheetSourceCandidate candidate,
        bool extractAfterNavigation)
    {
        await EnsureWebViewReadyAsync();
        var core = ChordWebView.CoreWebView2;
        if (core is null ||
            !Uri.TryCreate(candidate.SourceUrl, UriKind.Absolute, out var uri))
        {
            BrowserStatus.Text = "La dirección seleccionada no es válida.";
            return;
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void OnNavigationCompleted(
            object? sender,
            CoreWebView2NavigationCompletedEventArgs args) =>
            completion.TrySetResult(args.IsSuccess);
        core.NavigationCompleted += OnNavigationCompleted;
        try
        {
            AddressBox.Text = uri.AbsoluteUri;
            ChordWebView.Source = uri;
            var finished = await Task.WhenAny(
                completion.Task,
                Task.Delay(TimeSpan.FromSeconds(18)));
            if (finished != completion.Task || !await completion.Task)
            {
                BrowserStatus.Text =
                    "La página no terminó de cargar. Puedes revisarla o buscar otra opción.";
                return;
            }
            if (extractAfterNavigation)
            {
                await Task.Delay(900);
                await ExtractCurrentPageAsync();
            }
        }
        finally
        {
            core.NavigationCompleted -= OnNavigationCompleted;
        }
    }

    private void OnCurrentChordSheetLineChanged(
        object? sender,
        ChordSheetLineItem? line)
    {
        if (line is null || !IsVisible)
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            ChordLineList.ScrollIntoView(line);
        });
    }

    private void OnChordLineListPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (_viewModel.IsChordSheetFollowEnabled)
        {
            _viewModel.IsChordSheetFollowEnabled = false;
            BrowserStatus.Text =
                "Seguimiento pausado por desplazamiento manual. Actívalo de nuevo en la cabecera.";
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.CurrentChordSheetLineChanged -= OnCurrentChordSheetLineChanged;
        _viewModel.ChordSheetSourceOpenRequested -= OnChordSheetSourceOpenRequested;
        ChordWebView.Dispose();
    }

    private const string ExtractionScript =
        """
        (() => {
          const clean = value => (value || '')
            .replace(/\u00a0/g, ' ')
            .replace(/\r\n?/g, '\n')
            .trim();
          const selection = clean(window.getSelection()?.toString());
          if (selection.length >= 20) {
            return JSON.stringify({
              text: selection,
              title: document.title || '',
              url: location.href,
              method: 'selection'
            });
          }
          const selectors = [
            'pre',
            '[class*="chord"]',
            '[class*="Chord"]',
            '[class*="lyric"]',
            '[class*="Lyric"]',
            '[data-content]',
            'article',
            'main'
          ];
          const seen = new Set();
          const candidates = [];
          for (const selector of selectors) {
            for (const element of document.querySelectorAll(selector)) {
              if (seen.has(element)) continue;
              seen.add(element);
              const style = getComputedStyle(element);
              if (style.display === 'none' || style.visibility === 'hidden') continue;
              const text = clean(element.innerText);
              const lineCount = text.split('\n').filter(Boolean).length;
              if (text.length < 40 || lineCount < 3 || text.length > 120000) continue;
              const chordMatches = text.match(/(?:^|\s)[A-G](?:#|b)?(?:m|maj|min|dim|aug|sus|add)?\d*(?:\/[A-G](?:#|b)?)?(?=\s|$)/gm)?.length || 0;
              const score = chordMatches * 120 + lineCount * 4 + Math.min(text.length, 12000) / 80;
              candidates.push({ text, score });
            }
          }
          candidates.sort((left, right) => right.score - left.score);
          return JSON.stringify({
            text: candidates[0]?.text || '',
            title: document.title || '',
            url: location.href,
            method: candidates.length ? 'detected' : 'none'
          });
        })()
        """;

    private sealed class ExtractionPayload
    {
        public string Text { get; set; } = string.Empty;
        public string? Title { get; set; }
        public string? Url { get; set; }
    }
}
