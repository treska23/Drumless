using System.Windows;
using DrumPracticeStudio.ViewModels;

namespace DrumPracticeStudio.Views;

public partial class SongEffectProfileWindow : Window
{
    public SongEffectProfileWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
