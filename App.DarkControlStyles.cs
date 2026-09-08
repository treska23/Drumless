using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DrumPracticeStudio;

public partial class App
{
    public App()
    {
        // El estilo se instala cuando Application.Resources ya está cargado, pero antes
        // de crear MainWindow. De este modo entra en la cascada normal de recursos WPF
        // y no hace falta corregir controles después de que hayan aplicado su template.
        Startup += InstallDarkControlStyles;
    }

    private void InstallDarkControlStyles(object? sender, StartupEventArgs eventArgs)
    {
        Startup -= InstallDarkControlStyles;

        var expanderStyle = new Style(typeof(Expander));
        if (Resources.Contains(typeof(Expander)) &&
            Resources[typeof(Expander)] is Style existingExpanderStyle)
        {
            expanderStyle.BasedOn = existingExpanderStyle;
        }

        expanderStyle.Setters.Add(
            new Setter(Control.ForegroundProperty, Resources["TextPrimary"]));
        expanderStyle.Setters.Add(
            new Setter(HeaderedContentControl.HeaderTemplateProperty, CreateExpanderHeaderTemplate()));

        Resources[typeof(Expander)] = expanderStyle;
    }

    private static DataTemplate CreateExpanderHeaderTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding("."));
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        text.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);

        return new DataTemplate
        {
            VisualTree = text
        };
    }
}
