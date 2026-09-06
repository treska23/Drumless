using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.ViewModels;

namespace DrumPracticeStudio.Views;

public partial class PlaylistWindow : Window
{
    private readonly MainViewModel _viewModel;
    private Point? _dragOrigin;

    public static readonly DependencyProperty IsSelectionModeProperty =
        DependencyProperty.Register(nameof(IsSelectionMode), typeof(bool), typeof(PlaylistWindow),
            new PropertyMetadata(false));

    public bool IsSelectionMode
    {
        get => (bool)GetValue(IsSelectionModeProperty);
        set => SetValue(IsSelectionModeProperty, value);
    }

    public PlaylistWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        UpdateSelectionControls();
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e) =>
        _dragOrigin = IsSelectionMode && FindItem(e.OriginalSource) is not null &&
                      FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) is null
            ? e.GetPosition(FloatingPlaylistList)
            : null;

    private IReadOnlyList<PlaylistItemViewModel> SelectedItems()
    {
        var selected = FloatingPlaylistList.SelectedItems.Cast<PlaylistItemViewModel>()
            .Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        return FloatingPlaylistList.Items.Cast<PlaylistItemViewModel>()
            .Where(item => selected.Contains(item.Id)).ToArray();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewModel.SelectedPlaylistItem = FloatingPlaylistList.SelectedItem as PlaylistItemViewModel;
        UpdateSelectionControls();
    }

    private void UpdateSelectionControls()
    {
        var selected = SelectedItems();
        FloatingSelectionSummary.Text = selected.Count == 1
            ? "1 seleccionado"
            : $"{selected.Count} seleccionados";
        var single = selected.Count == 1;
        FloatingPlayButton.IsEnabled = !IsSelectionMode && single && selected[0].IsAvailable;
        var ids = selected.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        var items = _viewModel.SelectedPlaylist?.Items;
        FloatingMoveUpButton.IsEnabled = IsSelectionMode && items is not null &&
            items.Skip(1).Where((item, index) => ids.Contains(item.Id) && !ids.Contains(items[index].Id)).Any();
        FloatingMoveDownButton.IsEnabled = IsSelectionMode && items is not null &&
            items.Take(Math.Max(0, items.Count - 1))
                .Where((item, index) => ids.Contains(item.Id) && !ids.Contains(items[index + 1].Id)).Any();
        FloatingRemoveButton.IsEnabled = IsSelectionMode && selected.Count > 0;
    }

    private void OnToggleSelectionClick(object sender, RoutedEventArgs e) =>
        SetSelectionMode(!IsSelectionMode);

    private void SetSelectionMode(bool enabled)
    {
        _dragOrigin = null;
        if (enabled)
        {
            BindingOperations.ClearBinding(FloatingPlaylistList, Selector.SelectedItemProperty);
        }
        FloatingPlaylistList.UnselectAll();
        IsSelectionMode = enabled;
        FloatingPlaylistList.SelectionMode = enabled ? SelectionMode.Extended : SelectionMode.Single;
        if (!enabled)
        {
            FloatingPlaylistList.SetBinding(Selector.SelectedItemProperty,
                new Binding(nameof(MainViewModel.SelectedPlaylistItem)) { Mode = BindingMode.TwoWay });
        }
        FloatingSelectButton.Content = enabled ? "Hecho" : "Seleccionar";
        UpdateSelectionControls();
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        if (IsSelectionMode)
        {
            FloatingPlaylistList.SelectAll();
        }
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (IsSelectionMode && e.Key == Key.Escape)
        {
            SetSelectionMode(false);
            e.Handled = true;
        }
    }

    private void PreserveListState(Action mutation)
    {
        var playlist = _viewModel.SelectedPlaylist;
        var selectionMode = IsSelectionMode;
        ListSelectionState.Preserve<PlaylistItemViewModel>(FloatingPlaylistList, item => item.Id,
            mutation, () => ReferenceEquals(playlist, _viewModel.SelectedPlaylist) &&
                            selectionMode == IsSelectionMode);
    }

    private void OnPlaySelectionClick(object sender, RoutedEventArgs e) =>
        _viewModel.PlayPlaylistSelection(SelectedItems());

    private void OnMoveSelectionUpClick(object sender, RoutedEventArgs e) =>
        PreserveListState(() => _viewModel.MovePlaylistSelection(SelectedItems(), moveUp: true));

    private void OnMoveSelectionDownClick(object sender, RoutedEventArgs e) =>
        PreserveListState(() => _viewModel.MovePlaylistSelection(SelectedItems(), moveUp: false));

    private void OnRemoveSelectionClick(object sender, RoutedEventArgs e) =>
        PreserveListState(() => _viewModel.RemovePlaylistSelection(SelectedItems()));

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsSelectionMode || FloatingPlaylistList.SelectedItems.Count != 1 ||
            e.LeftButton != MouseButtonState.Pressed ||
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
            PreserveListState(() => _viewModel.AddTrackToSelectedPlaylist(track));
            e.Handled = true;
            return;
        }
        if (!IsSelectionMode || !e.Data.GetDataPresent(typeof(PlaylistItemViewModel)) ||
            e.Data.GetData(typeof(PlaylistItemViewModel)) is not PlaylistItemViewModel dragged)
        {
            return;
        }

        var target = FindItem(e.OriginalSource);
        var index = target is null
            ? _viewModel.PlaylistItems.Count - 1
            : _viewModel.PlaylistItems.IndexOf(target);
        PreserveListState(() => _viewModel.MoveSelectedPlaylistItem(dragged, index));
        e.Handled = true;
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsSelectionMode && e.ChangedButton == MouseButton.Left &&
            FindItem(e.OriginalSource) is { } item)
        {
            _viewModel.PlayPlaylistItem(item);
            e.Handled = true;
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
            element = ParentOf(element);
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T target)
            {
                return target;
            }
            element = ParentOf(element);
        }
        return null;
    }

    private static DependencyObject? ParentOf(DependencyObject element) => element is Visual
        ? VisualTreeHelper.GetParent(element)
        : LogicalTreeHelper.GetParent(element);
}
