using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio;

public partial class MainWindow
{
    internal static void InstallImportedVst3PickerGuard()
    {
        EventManager.RegisterClassHandler(
            typeof(ComboBox),
            Selector.SelectionChangedEvent,
            new SelectionChangedEventHandler(OnImportedVst3PickerSelectionChanged),
            handledEventsToo: true);
    }

    private static void OnImportedVst3PickerSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (sender is not ComboBox comboBox ||
            comboBox.Tag is not AudioEffectSlotItem slot ||
            slot.ExternalVst3 is null ||
            !slot.Id.StartsWith(
                AudioEffectPresetStore.ImportedSlotIdPrefix,
                StringComparison.Ordinal))
        {
            return;
        }

        // Al crear el ComboBox, WPF puede promover el CurrentItem de ListCollectionView antes de
        // que ConfigureVst3PickerSearch haya instalado su guardia de restauración. El handler XAML
        // interpretaba ese cambio interno como una elección del usuario y sustituía el VST3 que
        // acababa de venir del .dpsfx por el elemento actual del catálogo. Marcamos únicamente esa
        // fase inicial como manejada. En cuanto el picker queda configurado, las selecciones reales
        // del usuario vuelven a circular con normalidad.
        if (!Vst3PickerSearchStates.TryGetValue(comboBox, out _))
        {
            eventArgs.Handled = true;
        }
    }
}

internal static class ImportedVst3PickerGuardBootstrapper
{
    [ModuleInitializer]
    internal static void Initialize() =>
        MainWindow.InstallImportedVst3PickerGuard();
}
