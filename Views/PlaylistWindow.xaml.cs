using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.ViewModels;

namespace DrumPracticeStudio.Views;

public partial class PlaylistWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Point? _dragOrigin;

    public PlaylistWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e) =>
        _dragOrigin = e.GetPosition(FloatingPlaylistList);

    private IReadOnlyList<PlaylistItemViewModel> SelectedItems() =>
        FloatingPlaylistList.SelectedItems.Cast<PlaylistItemViewModel>().ToArray();

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = SelectedItems();
        _viewModel.SelectedPlaylistItem = selected.LastOrDefault();
        FloatingSelectionSummary.Text = selected.Count == 1
            ? "1 seleccionado"
            : $"{selected.Count} seleccionados";
        var single = selected.Count == 1;
        FloatingPlayButton.IsEnabled = single && selected[0].IsAvailable;
        FloatingMoveUpButton.IsEnabled = single;
        FloatingMoveDownButton.IsEnabled = single;
        FloatingRemoveButton.IsEnabled = selected.Count > 0;
    }

    private void OnPlaySelectionClick(object sender, RoutedEventArgs e) =>
        _viewModel.PlayPlaylistSelection(SelectedItems());

    private void OnMoveSelectionUpClick(object sender, RoutedEventArgs e) =>
        _viewModel.MovePlaylistSelection(SelectedItems(), moveUp: true);

    private void OnMoveSelectionDownClick(object sender, RoutedEventArgs e) =>
        _viewModel.MovePlaylistSelection(SelectedItems(), moveUp: false);

    private void OnRemoveSelectionClick(object sender, RoutedEventArgs e) =>
        _viewModel.RemovePlaylistSelection(SelectedItems());

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            _dragOrigin is not { } origin ||
            (Math.Abs(e.GetPosition(FloatingPlaylistList).X - origin.X) < SystemParameters.MinimumHorizontalDragDistance &&
             Math.Abs(e.GetPosition(FloatingPlaylistList).Y - origin.Y) < SystemParameters.MinimumVerticalDragDistance) ||
            FloatingPlaylistList.SelectedItem is not PlaylistItemViewModel item)
        {
            return;
        }

        _dragOrigin = null;
        DragDrop.DoDragDrop(FloatingPlaylistList, item, DragDropEffects.Move);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(LocalTrack)) &&
            e.Data.GetData(typeof(LocalTrack)) is LocalTrack track)
        {
            _viewModel.AddTrackToSelectedPlaylist(track);
            return;
        }
        if (!e.Data.GetDataPresent(typeof(PlaylistItemViewModel)) ||
            e.Data.GetData(typeof(PlaylistItemViewModel)) is not PlaylistItemViewModel dragged)
        {
            return;
        }

        var target = FindItem(e.OriginalSource);
        var index = target is null
            ? _viewModel.PlaylistItems.Count - 1
            : _viewModel.PlaylistItems.IndexOf(target);
        _viewModel.MoveSelectedPlaylistItem(dragged, index);
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FloatingPlaylistList.SelectedItem is PlaylistItemViewModel item)
        {
            _viewModel.PlayPlaylistItem(item);
        }
    }

    private void OnPlaylistMouseWheel(object sender, MouseWheelEventArgs e) =>
        MainWindow.ScrollPlaylistOrParent(sender as DependencyObject, e);

    private static PlaylistItemViewModel? FindItem(object source)
    {
        var element = source as DependencyObject;
        while (element is not null)
        {
            if (element is FrameworkElement { DataContext: PlaylistItemViewModel item })
            {
                return item;
            }
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }
}
