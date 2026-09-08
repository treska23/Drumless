using System.Windows;

namespace DrumPracticeStudio.Views;

public enum YouTubePlaylistImportMode
{
    CreateNew,
    UseExisting
}

public partial class YouTubePlaylistImportModeDialog : Window
{
    public YouTubePlaylistImportModeDialog(bool hasExistingPlaylists)
    {
        InitializeComponent();
        ExistingPlaylistButton.IsEnabled = hasExistingPlaylists;
        ExistingPlaylistButton.ToolTip = hasExistingPlaylists
            ? "Selecciona una o varias playlists existentes"
            : "Todavía no hay playlists creadas en Drumless";
    }

    public YouTubePlaylistImportMode? SelectedMode { get; private set; }

    private void OnCreateNewClick(object sender, RoutedEventArgs e)
    {
        SelectedMode = YouTubePlaylistImportMode.CreateNew;
        DialogResult = true;
    }

    private void OnUseExistingClick(object sender, RoutedEventArgs e)
    {
        SelectedMode = YouTubePlaylistImportMode.UseExisting;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
