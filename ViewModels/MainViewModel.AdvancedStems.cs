using System.Windows;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using DrumPracticeStudio.Views;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    private readonly AdvancedStemSeparationService _advancedStemSeparation = new();
    private RelayCommand? _createAdvancedStemsCommand;

    public RelayCommand CreateAdvancedStemsCommand =>
        _createAdvancedStemsCommand ??= new RelayCommand(() => _ = CreateAdvancedStemsAsync());

    public string AdvancedSeparationLabel =>
        "Mezcla avanzada con voz principal/coros y guitarra solista/rítmica";

    private async Task CreateAdvancedStemsAsync()
    {
        if (CurrentTrack is null)
        {
            StatusMessage = "Primero importa una pista local";
            return;
        }
        if (CurrentTrack.Variant != TrackVariant.Original)
        {
            StatusMessage = "La separación avanzada necesita la pista original";
            return;
        }
        if (CurrentTrack.IsMissing || !File.Exists(CurrentTrack.Path))
        {
            CurrentTrack.IsMissing = true;
            RefreshLibraryPresentation();
            SaveTrackWorkspace();
            StatusMessage = "El archivo original ha desaparecido; no se puede separar";
            return;
        }
        if (IsRemovingDrums)
        {
            return;
        }

        var initialSelection = AdvancedStemMixPlan.FromStandardSelection(SelectedStemSelection);
        var selectionDialog = new AdvancedStemSelectionDialog(initialSelection)
        {
            Owner = Application.Current?.MainWindow
        };
        if (selectionDialog.ShowDialog() != true)
        {
            return;
        }

        var advancedSelection = selectionDialog.Selection;
        var sourceTrack = CurrentTrack;
        _drumRemovalCancellation = new CancellationTokenSource();
        var cancellationToken = _drumRemovalCancellation.Token;
        var progress = new Progress<DrumRemovalProgress>(UpdateRemovalProgress);

        try
        {
            IsRemovingDrums = true;
            IsBusy = true;
            IsRemovalIndeterminate = false;
            RemovalProgress = 0d;

            if (!_drumRemoval.IsInstalled)
            {
                var answer = MessageBox.Show(
                    "La separación avanzada necesita primero Demucs para aislar los grupos principales. " +
                    "Se instalará en la carpeta privada de Drumless y no modificará el Python del sistema.\n\n¿Instalar Demucs ahora?",
                    "Instalar separación base",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (answer != MessageBoxResult.Yes)
                {
                    RemovalStatus = "Instalación cancelada";
                    return;
                }

                RemovalEngineStatus = "Instalando Demucs local…";
                await _drumRemoval.InstallAsync(progress, cancellationToken);
                RemovalEngineStatus = "Demucs local preparado";
            }

            if (!_advancedStemSeparation.IsInstalled)
            {
                var answer = MessageBox.Show(
                    "Para dividir voz principal/coros y guitarra solista/rítmica se instalará el motor avanzado. " +
                    "La instalación y la primera descarga de modelos se realizan una sola vez y quedan dentro de Drumless.\n\n¿Instalar ahora?",
                    "Instalar separación avanzada",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (answer != MessageBoxResult.Yes)
                {
                    RemovalStatus = "Instalación avanzada cancelada";
                    return;
                }

                RemovalEngineStatus = "Instalando separación avanzada…";
                await _advancedStemSeparation.InstallAsync(progress, cancellationToken);
                RemovalEngineStatus = "Demucs + separación avanzada preparados";
            }

            _desiredTrackPlaying = false;
            _activeRunGeneration = 0;
            _audio.StopTrack();
            var description = AdvancedStemMixPlan.Describe(advancedSelection);
            StatusMessage = $"Creando mezcla avanzada de {sourceTrack.Title}…";
            RemovalStatus = $"Archivo final: {description}";

            var result = await _advancedStemSeparation.CreateAsync(
                sourceTrack,
                OutputFolderPath,
                advancedSelection,
                progress,
                cancellationToken);

            var generatedTrack = _trackLibrary.RegisterGenerated(
                result.MixedPath,
                $"{sourceTrack.Title} · {description}");
            SaveTrackWorkspace();
            RefreshLibraryPresentation();

            if (CurrentTrack?.Id == sourceTrack.Id)
            {
                await LoadAndSelectTrackAsync(
                    generatedTrack,
                    autoPlay: false,
                    resetNavigation: true,
                    cancellationToken);
            }

            RemovalProgress = 1d;
            RemovalStatus = $"Mezcla avanzada creada · {description}";
            StatusMessage = $"Lista: {generatedTrack.Title}";
        }
        catch (OperationCanceledException)
        {
            RemovalStatus = "Separación avanzada cancelada; el original no se ha modificado";
            StatusMessage = "Separación avanzada cancelada";
        }
        catch (Exception exception)
        {
            RemovalStatus = $"Error: {exception.Message}";
            StatusMessage = "No se pudo completar la separación avanzada";
            MessageBox.Show(
                exception.Message,
                "Separación avanzada",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            IsRemovingDrums = false;
            IsBusy = false;
            _drumRemovalCancellation?.Dispose();
            _drumRemovalCancellation = null;
        }
    }
}
