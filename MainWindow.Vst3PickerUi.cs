using System.Collections;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    private static readonly ConditionalWeakTable<ComboBox, Vst3PickerSearchState>
        Vst3PickerSearchStates = new();

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(ComboBox),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(OnAnyComboBoxLoaded),
            handledEventsToo: true);
    }

    private static void OnAnyComboBoxLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox comboBox ||
            comboBox.Tag is not AudioEffectSlotItem slot)
        {
            return;
        }

        // El selector ya no hace de cuadro de búsqueda. Mantener ambas funciones en el mismo
        // ComboBox hacía que WPF refrescara la vista mientras se escribía, comiera caracteres y
        // perdiera SelectedItem. El ComboBox queda como selector puro.
        var explicitReference = slot.ExternalVst3;
        comboBox.IsEditable = false;
        comboBox.IsTextSearchEnabled = false;
        comboBox.StaysOpenOnEdit = false;
        comboBox.IsSynchronizedWithCurrentItem = false;

        if (explicitReference is null)
        {
            comboBox.SelectedIndex = -1;
            comboBox.Text = string.Empty;
        }

        // El Loaded del propio ComboBox termina primero de envolver ItemsSource en ListCollectionView.
        _ = comboBox.Dispatcher.BeginInvoke(
            DispatcherPriority.ContextIdle,
            new Action(() => ConfigureVst3PickerSearch(comboBox, slot, explicitReference)));
    }

    private static void ConfigureVst3PickerSearch(
        ComboBox comboBox,
        AudioEffectSlotItem slot,
        Vst3EffectReference? explicitReference)
    {
        if (Vst3PickerSearchStates.TryGetValue(comboBox, out _))
        {
            return;
        }

        var parent = FindDirectStackPanelParent(comboBox);
        if (parent is null)
        {
            return;
        }

        var comboIndex = parent.Children.IndexOf(comboBox);
        if (comboIndex < 0)
        {
            return;
        }

        var label = new TextBlock
        {
            Text = "Buscar plugin",
            FontSize = 9,
            Margin = new Thickness(0, 0, 0, 2),
            Foreground = comboBox.TryFindResource("TextSecondary") as Brush
                         ?? SystemColors.GrayTextBrush
        };

        var searchRow = new Grid
        {
            Margin = new Thickness(0, 0, 0, 5)
        };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var searchBox = new TextBox
        {
            MinHeight = 28,
            ToolTip = "Escribe el nombre, fabricante o tipo y pulsa Buscar"
        };
        Grid.SetColumn(searchBox, 0);
        searchRow.Children.Add(searchBox);

        var searchButton = new Button
        {
            Content = "Buscar",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            ToolTip = "Filtrar la lista de plugins"
        };
        searchButton.SetResourceReference(FrameworkElement.StyleProperty, "SecondaryButton");
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(searchButton);

        parent.Children.Insert(comboIndex, label);
        parent.Children.Insert(comboIndex + 1, searchRow);

        var state = new Vst3PickerSearchState(searchBox, label, searchButton, searchRow);
        Vst3PickerSearchStates.Add(comboBox, state);

        void RunSearch()
        {
            ApplyVst3PickerFilter(comboBox, searchBox.Text);
            comboBox.IsDropDownOpen = true;
        }

        searchButton.Click += (_, _) => RunSearch();
        searchBox.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key != Key.Enter)
            {
                return;
            }

            RunSearch();
            eventArgs.Handled = true;
        };

        comboBox.SelectionChanged += (_, _) =>
        {
            if (comboBox.SelectedItem is not Vst3EffectItem)
            {
                return;
            }

            // Tras elegir un plugin se restaura el catálogo completo. La selección permanece en
            // el ComboBox y una búsqueda posterior parte de cero.
            searchBox.Clear();
            ApplyVst3PickerFilter(comboBox, null);
        };

        comboBox.Unloaded += (_, _) =>
        {
            Vst3PickerSearchStates.Remove(comboBox);
        };

        RestoreExplicitVst3Selection(comboBox, slot, explicitReference);
    }

    private static void ApplyVst3PickerFilter(ComboBox comboBox, string? query)
    {
        if (comboBox.ItemsSource is not ListCollectionView view)
        {
            return;
        }

        var normalized = query?.Trim();
        view.Filter = string.IsNullOrWhiteSpace(normalized)
            ? null
            : item => item is Vst3EffectItem effect && effect.MatchesSearch(normalized);
    }

    private static void RestoreExplicitVst3Selection(
        ComboBox comboBox,
        AudioEffectSlotItem slot,
        Vst3EffectReference? explicitReference)
    {
        // Un slot recién creado debe quedarse vacío. El antiguo selector editable podía promover
        // el primer elemento actual de ICollectionView a SelectedItem y parecía que la aplicación
        // había elegido un plugin por su cuenta.
        if (explicitReference is null)
        {
            comboBox.SelectedIndex = -1;
            comboBox.Text = string.Empty;
            if (slot.ExternalVst3 is not null)
            {
                slot.ExternalVst3 = null;
            }
            return;
        }

        if (comboBox.ItemsSource is not IEnumerable items)
        {
            return;
        }

        var selected = items
            .Cast<object>()
            .OfType<Vst3EffectItem>()
            .FirstOrDefault(effect =>
                string.Equals(
                    effect.PluginClass.ClassId,
                    explicitReference.ClassId,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    effect.Module.Path,
                    explicitReference.ModulePath,
                    StringComparison.OrdinalIgnoreCase));

        comboBox.SelectedItem = selected;
    }

    private static StackPanel? FindDirectStackPanelParent(DependencyObject child)
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            if (current is StackPanel panel && child is UIElement element && panel.Children.Contains(element))
            {
                return panel;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private sealed class Vst3PickerSearchState(
        TextBox searchBox,
        TextBlock label,
        Button searchButton,
        Grid searchRow)
    {
        public TextBox SearchBox { get; } = searchBox;
        public TextBlock Label { get; } = label;
        public Button SearchButton { get; } = searchButton;
        public Grid SearchRow { get; } = searchRow;
    }
}
