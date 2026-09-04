using System.Collections;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    private static readonly ConditionalWeakTable<ComboBox, Vst3PickerSearchState>
        Vst3PickerSearchStates = new();

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
            Text = "Búsqueda flexible por nombre, marca o tipo · Intro para ver resultados",
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
            ToolTip = "No distingue mayúsculas, espacios ni tildes y tolera erratas. Pulsa Intro para abrir los resultados."
        };
        Grid.SetColumn(searchBox, 0);
        searchRow.Children.Add(searchBox);

        var searchButton = new Button
        {
            Content = "Ver resultados",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            ToolTip = "Abrir los plugins que coinciden con la búsqueda",
            IsEnabled = false
        };
        searchButton.SetResourceReference(FrameworkElement.StyleProperty, "SecondaryButton");
        Grid.SetColumn(searchButton, 1);
        searchRow.Children.Add(searchButton);

        parent.Children.Insert(comboIndex, label);
        parent.Children.Insert(comboIndex + 1, searchRow);

        var searchDelay = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        var state = new Vst3PickerSearchState(
            searchBox,
            label,
            searchButton,
            searchRow,
            searchDelay);
        Vst3PickerSearchStates.Add(comboBox, state);

        void ResetSearch(bool clearText)
        {
            searchDelay.Stop();
            if (clearText && searchBox.Text.Length > 0)
            {
                searchBox.Clear();
                return;
            }

            ApplyVst3PickerFilter(comboBox, null);
            if (slot.ExternalVst3 is { } selectedReference)
            {
                RestoreExplicitVst3Selection(
                    comboBox,
                    slot,
                    selectedReference);
            }
            comboBox.IsDropDownOpen = false;
            searchButton.IsEnabled = false;
            label.Text = "Búsqueda flexible por nombre, marca o tipo · Intro para ver resultados";
        }

        int UpdateFilter()
        {
            searchDelay.Stop();
            var query = searchBox.Text.Trim();
            if (query.Length == 0)
            {
                ResetSearch(clearText: false);
                label.Text = "Escribe algo antes de buscar; no se ejecutan búsquedas vacías.";
                return 0;
            }

            var matches = ApplyVst3PickerFilter(comboBox, query);
            label.Text = matches switch
            {
                < 0 => "No se pudo abrir el catálogo de plugins.",
                0 => $"Sin resultados para «{query}».",
                1 => $"1 plugin encontrado para «{query}» · pulsa Intro para abrirlo.",
                _ => $"{matches} plugins encontrados para «{query}» · pulsa Intro para abrirlos."
            };
            return matches;
        }

        void ShowResults()
        {
            var matches = UpdateFilter();
            comboBox.IsDropDownOpen = matches > 0;
        }

        // Mientras se escribe sólo se actualiza el filtro y el contador. El desplegable se abre
        // de forma explícita para que nunca tape ni robe el foco al cuadro de búsqueda.
        searchDelay.Tick += (_, _) => UpdateFilter();
        searchButton.Click += (_, _) => ShowResults();
        searchBox.TextChanged += (_, _) =>
        {
            searchDelay.Stop();
            var hasQuery = !string.IsNullOrWhiteSpace(searchBox.Text);
            searchButton.IsEnabled = hasQuery;
            if (!hasQuery)
            {
                ResetSearch(clearText: false);
                return;
            }

            label.Text = "Filtrando el catálogo…";
            searchDelay.Start();
        };
        searchBox.KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.Escape)
            {
                ResetSearch(clearText: true);
                eventArgs.Handled = true;
                return;
            }

            if (eventArgs.Key == Key.Enter)
            {
                ShowResults();
                eventArgs.Handled = true;
            }
        };

        comboBox.SelectionChanged += (_, _) =>
        {
            if (comboBox.SelectedItem is not Vst3EffectItem)
            {
                return;
            }

            // Tras elegir un plugin se restaura el catálogo completo. La selección permanece en
            // el ComboBox y una búsqueda posterior parte de cero.
            ResetSearch(clearText: true);
        };

        // No eliminamos el estado en Unloaded. ObservableCollection.Move puede descargar y volver a
        // cargar el mismo ComboBox sin retirar del panel el buscador que ya se inyectó. Si borramos
        // aquí la marca, el siguiente Loaded añade un segundo buscador. ConditionalWeakTable no
        // mantiene vivo el ComboBox: cuando WPF destruya realmente el control, la entrada desaparecerá.

        RestoreExplicitVst3Selection(comboBox, slot, explicitReference);
    }

    private static int ApplyVst3PickerFilter(ComboBox comboBox, string? query)
    {
        if (comboBox.ItemsSource is not ListCollectionView view)
        {
            return -1;
        }

        var normalized = query?.Trim();
        view.Filter = string.IsNullOrWhiteSpace(normalized)
            ? null
            : item => item is Vst3EffectItem effect && effect.MatchesSearch(normalized);
        return view.Count;
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
        Grid searchRow,
        System.Windows.Threading.DispatcherTimer searchDelay)
    {
        public TextBox SearchBox { get; } = searchBox;
        public TextBlock Label { get; } = label;
        public Button SearchButton { get; } = searchButton;
        public Grid SearchRow { get; } = searchRow;
        public System.Windows.Threading.DispatcherTimer SearchDelay { get; } = searchDelay;
    }
}
