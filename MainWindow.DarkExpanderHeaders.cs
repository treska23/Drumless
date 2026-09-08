using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DrumPracticeStudio;

/// <summary>
/// Define una sola regla de tema para los Expander de Drumless antes de que WPF
/// construya el template visual del control. Así el header no depende del
/// AccessText/ToggleButton del tema de Windows ni de cambios tardíos en Loaded.
/// </summary>
internal static class DarkExpanderHeadersBootstrapper
{
    private static readonly DataTemplate HeaderTemplate = CreateHeaderTemplate();

    [ModuleInitializer]
    internal static void Initialize()
    {
        EventManager.RegisterClassHandler(
            typeof(Expander),
            FrameworkElement.InitializedEvent,
            new EventHandler(OnExpanderInitialized));
    }

    private static void OnExpanderInitialized(object? sender, EventArgs eventArgs)
    {
        if (sender is not Expander expander)
        {
            return;
        }

        // El Foreground y el HeaderTemplate se fijan antes de ApplyTemplate.
        // No hay un segundo parche en Loaded que compita con la plantilla de Windows.
        expander.SetResourceReference(Control.ForegroundProperty, "TextPrimary");

        if (expander.Header is not FrameworkElement)
        {
            expander.HeaderTemplate = HeaderTemplate;
        }
    }

    private static DataTemplate CreateHeaderTemplate()
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
