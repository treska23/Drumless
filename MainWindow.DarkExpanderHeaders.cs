using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DrumPracticeStudio;

internal static class DarkExpanderHeadersBootstrapper
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(Expander),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnExpanderLoaded),
            handledEventsToo: true);
    }

    private static void OnExpanderLoaded(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is not Expander expander ||
            Window.GetWindow(expander) is not MainWindow)
        {
            return;
        }

        if (expander.TryFindResource("TextPrimary") is Brush foreground)
        {
            expander.Foreground = foreground;
        }
    }
}
