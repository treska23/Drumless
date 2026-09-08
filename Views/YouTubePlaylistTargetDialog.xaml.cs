using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Views;

public partial class YouTubePlaylistTargetDialog : Window
{
    private readonly YouTubePlaylistTargetDialogViewModel _viewModel;

    public YouTubePlaylistTargetDialog(IEnumerable<Playlist> playlists)
    {
        InitializeComponent();
        _viewModel = new YouTubePlaylistTargetDialogViewModel(playlists);
        DataContext = _viewModel;
        Loaded += (_, _) => PlaylistSearchBox.Focus();
    }

    public IReadOnlyList<Playlist> SelectedPlaylists => _viewModel.SelectedPlaylists;

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedPlaylists.Count == 0)
        {
            ValidationText.Text = "Marca al menos una playlist.";
            ValidationText.Visibility = Visibility.Visible;
            return;
        }

        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

internal sealed class YouTubePlaylistTargetDialogViewModel : ObservableObject
{
    private string _searchText = string.Empty;

    public YouTubePlaylistTargetDialogViewModel(IEnumerable<Playlist> playlists)
    {
        Options = new ObservableCollection<YouTubePlaylistTargetOption>(
            playlists
                .OrderBy(playlist => playlist.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(playlist => new YouTubePlaylistTargetOption(playlist)));

        VisibleOptions = CollectionViewSource.GetDefaultView(Options);
        VisibleOptions.Filter = FilterPlaylist;
    }

    public ObservableCollection<YouTubePlaylistTargetOption> Options { get; }

    public ICollectionView VisibleOptions { get; }

    public IReadOnlyList<Playlist> SelectedPlaylists => Options
        .Where(option => option.IsSelected)
        .Select(option => option.Playlist)
        .ToArray();

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                VisibleOptions.Refresh();
            }
        }
    }

    private bool FilterPlaylist(object item)
    {
        if (item is not YouTubePlaylistTargetOption option)
        {
            return false;
        }

        var search = SearchText.Trim();
        return string.IsNullOrWhiteSpace(search) ||
               option.Playlist.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase);
    }
}

internal sealed class YouTubePlaylistTargetOption : ObservableObject
{
    private bool _isSelected;

    public YouTubePlaylistTargetOption(Playlist playlist)
    {
        Playlist = playlist;
    }

    public Playlist Playlist { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string ItemCountLabel => Playlist.Items.Count == 1
        ? "1 elemento"
        : $"{Playlist.Items.Count} elementos";
}
