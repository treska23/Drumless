using System.Windows;
using System.Windows.Input;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Views;

public partial class PlaylistWindow
{
    private bool _directPlaybackFixAttached;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_directPlaybackFixAttached)
        {
            return;
        }

        _directPlaybackFixAttached = true;
        FloatingPlayButton.Click -= OnPlaySelectionClick;
        FloatingPlayButton.Click += OnPlaySelectionFixedClick;
        FloatingPlaylistList.MouseDoubleClick -= OnItemDoubleClick;
        FloatingPlaylistList.MouseDoubleClick += OnItemDoubleClickFixed;
    }

    private void OnPlaySelectionFixedClick(object sender, RoutedEventArgs e) =>
        _viewModel.PlayEditedPlaylistSelection(SelectedItems());

    private void OnItemDoubleClickFixed(object sender, MouseButtonEventArgs e)
    {
        if (FloatingPlaylistList.SelectedItem is PlaylistItemViewModel item)
        {
            _viewModel.PlayEditedPlaylistItem(item);
        }
    }
}
