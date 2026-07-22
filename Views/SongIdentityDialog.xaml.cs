using System.Windows;

namespace DrumPracticeStudio.Views;

public partial class SongIdentityDialog : Window
{
    public SongIdentityDialog(string suggestedArtist, string suggestedSongTitle)
    {
        InitializeComponent();
        ArtistTextBox.Text = suggestedArtist;
        SongTitleTextBox.Text = suggestedSongTitle;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(ArtistTextBox.Text))
            {
                ArtistTextBox.Focus();
            }
            else
            {
                SongTitleTextBox.Focus();
            }
        };
    }

    public string Artist => ArtistTextBox.Text.Trim();
    public string SongTitle => SongTitleTextBox.Text.Trim();

    private void OnContinueClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Artist) || string.IsNullOrWhiteSpace(SongTitle))
        {
            ValidationText.Text = "Completa el intérprete y el nombre de la canción.";
            ValidationText.Visibility = Visibility.Visible;
            return;
        }
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
