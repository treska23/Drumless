using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
            Window.GetWindow(expander) is not MainWindow ||
            expander.TryFindResource("TextPrimary") is not Brush foreground)
        {
            return;
        }

        // El template estándar de Expander puede pintar su AccessText/HeaderPresenter con
        // un brush del tema de Windows aunque Foreground del Expander sea claro. Por eso
        // establecer sólo expander.Foreground no basta en el tema oscuro de Drumless.
        expander.Foreground = foreground;

        // Forzamos el elemento visual que dibuja el header. El DataContext de HeaderTemplate
        // es el propio Header (en estos buses es el string "Pista local", "YouTube", etc.).
        // Un Foreground local en este TextBlock tiene prioridad sobre cualquier estilo o
        // template intermedio que estuviera reintroduciendo texto negro.
        if (expander.HeaderTemplate is null && expander.Header is not FrameworkElement)
        {
            var headerText = new FrameworkElementFactory(typeof(TextBlock));
            headerText.SetBinding(TextBlock.TextProperty, new Binding("."));
            headerText.SetValue(TextBlock.ForegroundProperty, foreground);
            headerText.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

            expander.HeaderTemplate = new DataTemplate
            {
                VisualTree = headerText
            };
        }
    }
}
