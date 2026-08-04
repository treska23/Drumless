using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DrumPracticeStudio.Controls;
using DrumPracticeStudio.Services;
using DrumPracticeStudio.ViewModels;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    private bool _drumJourneyUiAttached;
    private DrumJourneyVisualizer? _drumJourneyVisualizer;
    private MainViewModel? _drumJourneyViewModel;

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnDrumJourneyWindowLoaded));
    }

    private static void OnDrumJourneyWindowLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not MainWindow window || window._drumJourneyUiAttached)
        {
            return;
        }

        _ = window.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(window.AttachDrumJourneyUi));
    }

    private void AttachDrumJourneyUi()
    {
        if (_drumJourneyUiAttached || PracticeMainColumn is null)
        {
            return;
        }

        _drumJourneyUiAttached = true;
        const int insertionRow = 3;
        if (PracticeMainColumn.RowDefinitions.Count < insertionRow)
        {
            return;
        }

        var existingChildren = PracticeMainColumn.Children
            .OfType<UIElement>()
            .ToArray();
        foreach (var child in existingChildren)
        {
            var currentRow = Grid.GetRow(child);
            if (currentRow >= insertionRow)
            {
                Grid.SetRow(child, currentRow + 1);
            }
        }

        PracticeMainColumn.RowDefinitions.Insert(
            insertionRow,
            new RowDefinition
            {
                Height = new GridLength(0.92d, GridUnitType.Star),
                MinHeight = 210d
            });

        _drumJourneyVisualizer = new DrumJourneyVisualizer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        var card = new Border
        {
            Child = _drumJourneyVisualizer,
            ClipToBounds = true,
            Padding = new Thickness(0d),
            Margin = new Thickness(0d, 0d, 0d, 20d),
            CornerRadius = new CornerRadius(12d)
        };
        if (TryFindResource("CardStyle") is Style cardStyle)
        {
            card.Style = cardStyle;
            card.Padding = new Thickness(0d);
        }

        Grid.SetRow(card, insertionRow);
        PracticeMainColumn.Children.Add(card);

        _drumJourneyViewModel = _viewModel;
        _drumJourneyViewModel.DrumJourneyHitProduced += OnDrumJourneyHitProduced;
        _drumJourneyViewModel.DrumJourneyStateChanged += OnDrumJourneyStateChanged;
        _drumJourneyViewModel.AttachDrumJourney();
        Closed += OnDrumJourneyWindowClosed;
    }

    private void OnDrumJourneyHitProduced(object? sender, DrumJourneyHitEvent hit)
    {
        if (_drumJourneyVisualizer is null)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            _drumJourneyVisualizer.PushHit(hit);
        }
        else
        {
            _ = Dispatcher.BeginInvoke(() => _drumJourneyVisualizer?.PushHit(hit));
        }
    }

    private void OnDrumJourneyStateChanged(object? sender, DrumJourneyState state)
    {
        if (_drumJourneyVisualizer is null)
        {
            return;
        }

        if (Dispatcher.CheckAccess())
        {
            _drumJourneyVisualizer.UpdateState(state);
        }
        else
        {
            _ = Dispatcher.BeginInvoke(() => _drumJourneyVisualizer?.UpdateState(state));
        }
    }

    private void OnDrumJourneyWindowClosed(object? sender, EventArgs eventArgs)
    {
        Closed -= OnDrumJourneyWindowClosed;
        if (_drumJourneyViewModel is not null)
        {
            _drumJourneyViewModel.DrumJourneyHitProduced -= OnDrumJourneyHitProduced;
            _drumJourneyViewModel.DrumJourneyStateChanged -= OnDrumJourneyStateChanged;
            _drumJourneyViewModel.DetachDrumJourney();
        }
        _drumJourneyViewModel = null;
        _drumJourneyVisualizer = null;
    }
}
