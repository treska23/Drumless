using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    public void PlayEditedPlaylistSelection(IReadOnlyList<PlaylistItemViewModel> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count != 1)
        {
            StatusMessage = items.Count == 0
                ? "Selecciona un elemento de la playlist"
                : "Selecciona solo un elemento para reproducirlo";
            return;
        }

        PlayEditedPlaylistItem(items[0]);
    }

    public void PlayEditedPlaylistItem(PlaylistItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _ = PlayEditedPlaylistItemAsync(item);
    }

    private async Task PlayEditedPlaylistItemAsync(PlaylistItemViewModel item)
    {
        if (SelectedPlaylist is null)
        {
            StatusMessage = "Selecciona una playlist primero";
            return;
        }

        var sourceItem = SelectedPlaylist.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, item.Id, StringComparison.Ordinal));
        if (sourceItem is null)
        {
            StatusMessage = $"{item.Title} ya no pertenece a la playlist seleccionada";
            return;
        }

        if (!IsPlaylistItemAvailable(sourceItem))
        {
            StatusMessage = sourceItem.Kind == PlaylistItemKind.LocalTrack
                ? $"No se encuentra el archivo de {item.Title}"
                : $"{item.Title} no tiene un enlace de YouTube válido";
            return;
        }

        ResetEditedPlaylistPlaybackQueue(item.Id);
        await PlayNavigationTargetAsync(item.Id, autoPlayLocal: true);
    }

    private void ResetEditedPlaylistPlaybackQueue(string currentItemId)
    {
        var items = SelectedPlaylist?.Items
            .Where(IsPlaylistItemAvailable)
            .ToArray() ?? [];

        _playlistPlaybackItems.Clear();
        foreach (var item in items)
        {
            _playlistPlaybackItems[item.Id] = item;
        }

        _playlistQueueActive = true;
        _playbackNavigator.SetQueue(items.Select(item => item.Id), currentItemId);
    }
}
