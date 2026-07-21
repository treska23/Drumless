using System.Collections;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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

        // The editable ComboBox was doing two incompatible jobs at once: search text and
        // actual selection. Refreshing its filtered ICollectionView while typing could move
        // the caret, eat characters and clear SelectedItem. Keep it as a pure selector.
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

        // Let the instance Loaded handler finish wrapping ItemsSource in ListCollectionView,
        // then add the independent search box next to the finished picker.
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
        var searchBox = new TextBox
        {
            MinHeight = 28,
            Margin = new Thickness(0, 0, 0, 5),
            ToolTip = "Escribe para filtrar por nombre, fabricante o tipo"
        };

        parent.Children.Insert(comboIndex, label);
        parent.Children.Insert(comboIndex + 1, searchBox);

        var timer = new DispatcherTimer(
            DispatcherPriority.Background,
            comboBox.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        var state = new Vst3PickerSearchState(searchBox, label, timer);
        Vst3PickerSearchStates.Add(comboBox, state);

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyVst3PickerFilter(comboBox, searchBox.Text);
            if (!string.IsNullOrWhiteSpace(searchBox.Text) &&
                searchBox.IsKeyboardFocusWithin)
            {
                comboBox.IsDropDownOpen = true;
            }
        };

        searchBox.TextChanged += (_, _) =>
        {
            if (state.SuppressSearchChange)
            {
                return;
            }
            timer.Stop();
            timer.Start();
        };

        comboBox.SelectionChanged += (_, _) =>
        {
            if (comboBox.SelectedItem is not Vst3EffectItem)
            {
                return;
            }

            timer.Stop();
            state.SuppressSearchChange = true;
            searchBox.Clear();
            state.SuppressSearchChange = false;
            ApplyVst3PickerFilter(comboBox, null);
        };

        comboBox.Unloaded += (_, _) =>
        {
            timer.Stop();
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
        // A newly-created empty slot must stay empty. In the old editable/synchronised picker,
        // ICollectionView could promote its first current item into SelectedItem (often a Waves
        // plug-in), making it look as though the app had chosen a plug-in by itself.
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
        DispatcherTimer timer)
    {
        public TextBox SearchBox { get; } = searchBox;
        public TextBlock Label { get; } = label;
        public DispatcherTimer Timer { get; } = timer;
        public bool SuppressSearchChange { get; set; }
    }
}
