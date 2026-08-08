using System.Windows;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Views;

public partial class AdvancedStemSelectionDialog : Window
{
    public AdvancedStemSelectionDialog(AdvancedStemSelection initialSelection)
    {
        InitializeComponent();
        ApplySelection(initialSelection == AdvancedStemSelection.None
            ? AdvancedStemSelection.All
            : initialSelection);
        UpdateSummary();
    }

    public AdvancedStemSelection Selection { get; private set; }

    private void ApplySelection(AdvancedStemSelection selection)
    {
        DrumsCheckBox.IsChecked = selection.HasFlag(AdvancedStemSelection.Drums);
        BassCheckBox.IsChecked = selection.HasFlag(AdvancedStemSelection.Bass);
        LeadVocalCheckBox.IsChecked = selection.HasFlag(AdvancedStemSelection.LeadVocal);
        BackVocalCheckBox.IsChecked = selection.HasFlag(AdvancedStemSelection.BackVocal);
        LeadGuitarCheckBox.IsChecked = selection.HasFlag(AdvancedStemSelection.LeadGuitar);
        RhythmGuitarCheckBox.IsChecked = selection.HasFlag(AdvancedStemSelection.RhythmGuitar);
        PianoCheckBox.IsChecked = selection.HasFlag(AdvancedStemSelection.Piano);
        OtherCheckBox.IsChecked = selection.HasFlag(AdvancedStemSelection.Other);
    }

    private AdvancedStemSelection ReadSelection()
    {
        var selection = AdvancedStemSelection.None;
        if (DrumsCheckBox.IsChecked == true) selection |= AdvancedStemSelection.Drums;
        if (BassCheckBox.IsChecked == true) selection |= AdvancedStemSelection.Bass;
        if (LeadVocalCheckBox.IsChecked == true) selection |= AdvancedStemSelection.LeadVocal;
        if (BackVocalCheckBox.IsChecked == true) selection |= AdvancedStemSelection.BackVocal;
        if (LeadGuitarCheckBox.IsChecked == true) selection |= AdvancedStemSelection.LeadGuitar;
        if (RhythmGuitarCheckBox.IsChecked == true) selection |= AdvancedStemSelection.RhythmGuitar;
        if (PianoCheckBox.IsChecked == true) selection |= AdvancedStemSelection.Piano;
        if (OtherCheckBox.IsChecked == true) selection |= AdvancedStemSelection.Other;
        return selection;
    }

    private void UpdateSummary()
    {
        if (SelectionSummary is null || CreateButton is null)
        {
            return;
        }

        var selection = ReadSelection();
        CreateButton.IsEnabled = selection != AdvancedStemSelection.None;
        SelectionSummary.Text = selection == AdvancedStemSelection.None
            ? "Selecciona al menos una parte."
            : $"Archivo final: {AdvancedStemMixPlan.Describe(selection)}";
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e) => UpdateSummary();

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        ApplySelection(AdvancedStemSelection.All);
        UpdateSummary();
    }

    private void OnCreateClick(object sender, RoutedEventArgs e)
    {
        var selection = ReadSelection();
        if (selection == AdvancedStemSelection.None)
        {
            MessageBox.Show(
                this,
                "Selecciona al menos una parte para crear el archivo final.",
                "Separación avanzada",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        Selection = selection;
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
