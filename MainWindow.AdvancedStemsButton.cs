using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    private bool _advancedStemButtonInjected;
    private bool _libraryRemovalRewired;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        RewireLibraryRemoval();
        if (_advancedStemButtonInjected)
        {
            return;
        }

        var standardButton = FindByAutomationId<Button>(this, "CreateStemMixButton");
        if (standardButton?.Parent is not Grid parent)
        {
            return;
        }

        var row = Grid.GetRow(standardButton);
        var column = Grid.GetColumn(standardButton);
        var rowSpan = Grid.GetRowSpan(standardButton);
        var columnSpan = Grid.GetColumnSpan(standardButton);
        parent.Children.Remove(standardButton);

        var buttons = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(buttons, row);
        Grid.SetColumn(buttons, column);
        Grid.SetRowSpan(buttons, rowSpan);
        Grid.SetColumnSpan(buttons, columnSpan);

        standardButton.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(standardButton);

        var advancedButton = new Button
        {
            Content = "Crear mezcla avanzada",
            Padding = new Thickness(12, 8, 12, 8),
            ToolTip = "Elige qué conservar: voz principal, coros, guitarra solista, guitarra rítmica y los stems base. Solo se añade la mezcla final."
        };
        advancedButton.SetResourceReference(FrameworkElement.StyleProperty, "SecondaryButton");
        advancedButton.Click += OnCreateAdvancedStemMixClick;
        AutomationProperties.SetAutomationId(advancedButton, "CreateAdvancedStemsButton");
        buttons.Children.Add(advancedButton);
        parent.Children.Add(buttons);
        _advancedStemButtonInjected = true;
    }

    private void OnCreateAdvancedStemMixClick(object sender, RoutedEventArgs eventArgs)
    {
        _viewModel.CreateAdvancedStemsCommand.Execute(null);
        eventArgs.Handled = true;
    }

    private void RewireLibraryRemoval()
    {
        if (_libraryRemovalRewired)
        {
            return;
        }

        _viewModel.EnablePermanentTrackDeletionPrompt();
        RemoveLibrarySelectionButton.Click -= OnRemoveLibrarySelectionClick;
        RemoveLibrarySelectionButton.Click += OnRemoveLibrarySelectionWithDeletePromptClick;
        RemoveLibrarySelectionButton.ToolTip =
            "Quitar de la biblioteca o eliminar también el archivo del disco";
        _libraryRemovalRewired = true;
    }

    private void OnRemoveLibrarySelectionWithDeletePromptClick(
        object sender,
        RoutedEventArgs eventArgs)
    {
        _viewModel.RemoveLibrarySelectionWithDeletePrompt(GetSelectedLibraryTracks());
        UpdateLibrarySelectionControls();
    }

    private static T? FindByAutomationId<T>(DependencyObject root, string automationId)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed &&
                string.Equals(
                    AutomationProperties.GetAutomationId(typed),
                    automationId,
                    StringComparison.Ordinal))
            {
                return typed;
            }

            var nested = FindByAutomationId<T>(child, automationId);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }
}
