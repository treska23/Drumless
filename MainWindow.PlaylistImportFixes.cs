using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    private Button? _fixedYouTubePlaylistImportButton;
    private bool _playlistInteractionFixesAttached;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        AttachYouTubeDirectOutputRouting();
        AttachYouTubePlaybackReliabilityFixes();
        AttachAdvancedStemUiFixes();
        if (_playlistInteractionFixesAttached)
        {
            return;
        }

        _playlistInteractionFixesAttached = true;

        PlayPlaylistSelectionButton.Click -= OnPlayPlaylistSelectionClick;
        PlayPlaylistSelectionButton.Click += OnPlayPlaylistSelectionFixedClick;
        PlaylistItemList.MouseDoubleClick -= OnPlaylistItemDoubleClick;
        PlaylistItemList.MouseDoubleClick += OnPlaylistItemDoubleClickFixed;

        _fixedYouTubePlaylistImportButton = FindDescendantByAutomationId<Button>(
            this,
            "ImportYouTubePlaylistButton");
        if (_fixedYouTubePlaylistImportButton is not null)
        {
            _fixedYouTubePlaylistImportButton.Click -= OnImportCurrentYouTubePlaylistClick;
            _fixedYouTubePlaylistImportButton.Click += OnImportCurrentYouTubePlaylistFixedClick;
        }
    }

    private void OnPlayPlaylistSelectionFixedClick(object sender, RoutedEventArgs e) =>
        _viewModel.PlayEditedPlaylistSelection(GetSelectedPlaylistItems());

    private void OnPlaylistItemDoubleClickFixed(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistItemList.SelectedItem is PlaylistItemViewModel item)
        {
            _viewModel.PlayEditedPlaylistItem(item);
        }
    }

    private async void OnImportCurrentYouTubePlaylistFixedClick(object sender, RoutedEventArgs e)
    {
        var targetPlaylist = _viewModel.SelectedPlaylist;
        if (targetPlaylist is null)
        {
            ShowYouTubePlaylistImportMessage(
                "Selecciona primero, en el panel Playlists, la playlist de Drumless a la que quieres añadir los vídeos.",
                MessageBoxImage.Information);
            return;
        }

        if (!YouTubeNavigationService.TryGetPlaylistId(YouTubeWebView.Source, out _))
        {
            ShowYouTubePlaylistImportMessage(
                "Abre una playlist de YouTube o pega su URL en el buscador antes de pulsar Importar playlist completa.",
                MessageBoxImage.Information);
            return;
        }

        if (YouTubeWebView.CoreWebView2 is null)
        {
            ShowYouTubePlaylistImportMessage(
                "El navegador de YouTube todavía no está preparado.",
                MessageBoxImage.Warning);
            return;
        }

        var button = sender as Button ?? _fixedYouTubePlaylistImportButton;
        if (button is not null)
        {
            button.IsEnabled = false;
        }

        try
        {
            YouTubeStatusText.Text =
                $"Importando la playlist de YouTube en «{targetPlaylist.Name}»…";
            var payload = await ExtractCurrentYouTubePlaylistAsync();
            if (payload?.Items is not { Count: > 0 })
            {
                ShowYouTubePlaylistImportMessage(
                    "No se encontraron vídeos. Espera a que YouTube termine de cargar la playlist y vuelve a intentarlo.",
                    MessageBoxImage.Warning);
                return;
            }

            if (!ReferenceEquals(_viewModel.SelectedPlaylist, targetPlaylist))
            {
                ShowYouTubePlaylistImportMessage(
                    "La playlist seleccionada cambió mientras se estaba leyendo YouTube. No se ha importado nada; vuelve a pulsar el botón.",
                    MessageBoxImage.Warning);
                return;
            }

            var entries = payload.Items
                .Select(item => TryCreateYouTubePlaylistEntry(item, out var entry) ? entry : null)
                .Where(entry => entry is not null)
                .Cast<YouTubePlaylistEntry>()
                .DistinctBy(entry => entry.VideoId, StringComparer.Ordinal)
                .ToArray();
            if (entries.Length == 0)
            {
                ShowYouTubePlaylistImportMessage(
                    "YouTube no devolvió vídeos válidos para importar.",
                    MessageBoxImage.Warning);
                return;
            }

            var sourceName = string.IsNullOrWhiteSpace(payload.Title)
                ? "Playlist de YouTube"
                : payload.Title.Trim();
            var result = _viewModel.ImportYouTubePlaylist(entries, sourceName);
            var message = result.Added switch
            {
                0 => $"No se añadió ningún vídeo a «{targetPlaylist.Name}»: los {result.Duplicates} ya estaban incluidos.",
                1 when result.Duplicates == 0 => $"Se añadió 1 vídeo a «{targetPlaylist.Name}».",
                1 => $"Se añadió 1 vídeo a «{targetPlaylist.Name}» y se omitieron {result.Duplicates} duplicados.",
                _ when result.Duplicates == 0 => $"Se añadieron {result.Added} vídeos a «{targetPlaylist.Name}».",
                _ => $"Se añadieron {result.Added} vídeos a «{targetPlaylist.Name}» y se omitieron {result.Duplicates} duplicados."
            };
            ShowYouTubePlaylistImportMessage(message, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            ShowYouTubePlaylistImportMessage(
                $"No se pudo importar la playlist: {exception.Message}",
                MessageBoxImage.Error);
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
            }
        }
    }

    private async Task<YouTubePlaylistPayload?> ExtractCurrentYouTubePlaylistAsync()
    {
        var core = YouTubeWebView.CoreWebView2;
        if (core is null)
        {
            return null;
        }

        var collected = new Dictionary<string, YouTubePlaylistItemPayload>(StringComparer.Ordinal);
        var playlistTitle = string.Empty;
        var previousCount = -1;
        var stablePasses = 0;

        for (var pass = 0; pass < 100 && (pass < 10 || stablePasses < 5); pass++)
        {
            var scriptResult = await core.ExecuteScriptAsync(YouTubePlaylistSnapshotAndScrollScript);
            var payloadJson = JsonSerializer.Deserialize<string>(scriptResult);
            var payload = string.IsNullOrWhiteSpace(payloadJson)
                ? null
                : JsonSerializer.Deserialize<YouTubePlaylistPayload>(
                    payloadJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (payload is not null)
            {
                if (string.IsNullOrWhiteSpace(playlistTitle) &&
                    !string.IsNullOrWhiteSpace(payload.Title))
                {
                    playlistTitle = payload.Title.Trim();
                }

                foreach (var item in payload.Items)
                {
                    if (!string.IsNullOrWhiteSpace(item.VideoId))
                    {
                        collected[item.VideoId] = item;
                    }
                }
            }

            stablePasses = collected.Count == previousCount ? stablePasses + 1 : 0;
            previousCount = collected.Count;
            YouTubeStatusText.Text = collected.Count == 0
                ? $"Buscando vídeos en la playlist… intento {pass + 1}"
                : $"Leyendo la playlist completa… {collected.Count} vídeos encontrados";

            if (pass < 99 && (pass < 9 || stablePasses < 5))
            {
                await Task.Delay(350);
            }
        }

        return new YouTubePlaylistPayload
        {
            Title = playlistTitle,
            Items = collected.Values.ToList()
        };
    }

    private void ShowYouTubePlaylistImportMessage(string message, MessageBoxImage image)
    {
        YouTubeStatusText.Text = message;
        MessageBox.Show(
            this,
            message,
            "Importar playlist de YouTube",
            MessageBoxButton.OK,
            image);
    }

    private static T? FindDescendantByAutomationId<T>(
        DependencyObject parent,
        string automationId)
        where T : FrameworkElement
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match &&
                string.Equals(
                    AutomationProperties.GetAutomationId(match),
                    automationId,
                    StringComparison.Ordinal))
            {
                return match;
            }

            if (FindDescendantByAutomationId<T>(child, automationId) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private const string YouTubePlaylistSnapshotAndScrollScript =
        """
        (() => {
          const renderers = Array.from(document.querySelectorAll(
            'ytd-playlist-video-renderer, ytd-playlist-panel-video-renderer'));
          const seen = new Set();
          const items = [];
          for (const node of renderers) {
            const link = node.querySelector('a#video-title, a#wc-endpoint, a[href*="watch?v="]');
            if (!link?.href) continue;
            const url = new URL(link.href, location.origin);
            const videoId = url.searchParams.get('v') || node.getAttribute('video-id') || '';
            if (!videoId || seen.has(videoId)) continue;
            seen.add(videoId);
            const titleNode = node.querySelector('#video-title');
            const title = (titleNode?.getAttribute('title') || titleNode?.textContent || '').trim();
            const image = node.querySelector('img');
            items.push({
              videoId,
              title,
              url: `https://www.youtube.com/watch?v=${encodeURIComponent(videoId)}`,
              thumbnailUrl: image?.currentSrc || image?.src || ''
            });
          }

          for (const selector of [
            'ytd-playlist-video-list-renderer #contents',
            'ytd-playlist-panel-renderer #items'
          ]) {
            const container = document.querySelector(selector);
            container?.lastElementChild?.scrollIntoView({ block: 'end' });
          }
          window.scrollTo(0, document.documentElement.scrollHeight);

          const title = (
            document.querySelector('ytd-playlist-header-renderer h1 yt-formatted-string')?.textContent ||
            document.querySelector('ytd-playlist-panel-renderer #title')?.textContent ||
            document.title.replace(/\s*-\s*YouTube\s*$/i, '')
          ).trim();
          return JSON.stringify({ title, items });
        })()
        """;
}
