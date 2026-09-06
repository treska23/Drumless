using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.ViewModels;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    public static readonly DependencyProperty IsLibrarySelectionModeProperty =
        DependencyProperty.Register(nameof(IsLibrarySelectionMode), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false));

    public static readonly DependencyProperty IsPlaylistSelectionModeProperty =
        DependencyProperty.Register(nameof(IsPlaylistSelectionMode), typeof(bool), typeof(MainWindow),
            new PropertyMetadata(false));

    public bool IsLibrarySelectionMode
    {
        get => (bool)GetValue(IsLibrarySelectionModeProperty);
        private set => SetValue(IsLibrarySelectionModeProperty, value);
    }

    public bool IsPlaylistSelectionMode
    {
        get => (bool)GetValue(IsPlaylistSelectionModeProperty);
        private set => SetValue(IsPlaylistSelectionModeProperty, value);
    }

    private void OnToggleLibrarySelectionMode(object sender, RoutedEventArgs e) =>
        SetLibrarySelectionMode(!IsLibrarySelectionMode);

    private void SetLibrarySelectionMode(bool enabled)
    {
        IsLibrarySelectionMode = enabled;
        _libraryDragOrigin = null;
        BindingOperations.ClearBinding(TrackLibraryList, Selector.SelectedItemProperty);
        TrackLibraryList.UnselectAll();
        TrackLibraryList.SelectionMode = enabled ? SelectionMode.Extended : SelectionMode.Single;
        if (!enabled)
        {
            TrackLibraryList.SetBinding(Selector.SelectedItemProperty,
                new Binding(nameof(MainViewModel.SelectedLibraryTrack)) { Mode = BindingMode.TwoWay });
        }
        LibrarySelectionModeButton.Content = enabled ? "Hecho" : "Seleccionar";
    }

    private void OnTogglePlaylistSelectionMode(object sender, RoutedEventArgs e) =>
        SetPlaylistSelectionMode(!IsPlaylistSelectionMode);

    private void SetPlaylistSelectionMode(bool enabled)
    {
        IsPlaylistSelectionMode = enabled;
        _playlistDragOrigin = null;
        BindingOperations.ClearBinding(PlaylistItemList, Selector.SelectedItemProperty);
        PlaylistItemList.UnselectAll();
        PlaylistItemList.SelectionMode = enabled ? SelectionMode.Extended : SelectionMode.Single;
        if (!enabled)
        {
            PlaylistItemList.SetBinding(Selector.SelectedItemProperty,
                new Binding(nameof(MainViewModel.SelectedPlaylistItem)) { Mode = BindingMode.TwoWay });
        }
        PlaylistSelectionModeButton.Content = enabled ? "Hecho" : "Seleccionar";
    }

    private void OnSelectAllLibraryTracks(object sender, RoutedEventArgs e)
    {
        if (IsLibrarySelectionMode) TrackLibraryList.SelectAll();
    }

    private void OnSelectAllPlaylistItems(object sender, RoutedEventArgs e)
    {
        if (IsPlaylistSelectionMode) PlaylistItemList.SelectAll();
    }

    private void OnLibraryItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsLibrarySelectionMode && e.ChangedButton == MouseButton.Left &&
            FindItemFromSource<LocalTrack>(TrackLibraryList, e.OriginalSource) is { } track)
        {
            _viewModel.LoadLibrarySelection([track]);
            e.Handled = true;
        }
    }

    private void OnLibraryListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsLibrarySelectionMode)
        {
            SetLibrarySelectionMode(false);
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && IsLibrarySelectionMode)
        {
            TrackLibraryList.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !IsLibrarySelectionMode)
        {
            _viewModel.LoadLibrarySelection(GetSelectedLibraryTracks());
            e.Handled = true;
        }
    }

    private void OnPlaylistListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsPlaylistSelectionMode)
        {
            SetPlaylistSelectionMode(false);
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && IsPlaylistSelectionMode)
        {
            PlaylistItemList.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !IsPlaylistSelectionMode)
        {
            _viewModel.PlayEditedPlaylistSelection(GetSelectedPlaylistItems());
            e.Handled = true;
        }
    }

    private void PreserveLibraryList(Action mutation)
    {
        var selectionMode = IsLibrarySelectionMode;
        PreservePlaylistList(() =>
            ListSelectionState.Preserve<LocalTrack>(TrackLibraryList, track => track.Id, mutation,
                () => selectionMode == IsLibrarySelectionMode));
    }

    private void PreservePlaylistList(Action mutation)
    {
        var playlist = _viewModel.SelectedPlaylist;
        var selectionMode = IsPlaylistSelectionMode;
        ListSelectionState.Preserve<PlaylistItemViewModel>(PlaylistItemList, item => item.Id, mutation,
            () => ReferenceEquals(playlist, _viewModel.SelectedPlaylist) &&
                  selectionMode == IsPlaylistSelectionMode);
    }
}
