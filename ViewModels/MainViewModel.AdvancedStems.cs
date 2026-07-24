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
        _createAdvancedStemsCommand ??= new RelayCommand(ExecuteCreateAdvancedStems);

    public string AdvancedSeparationLabel =>
        "Mezcla avanzada con voz principal/coros y guitarra solista/rítmica";

    private async void ExecuteCreateAdvancedStems()
    {
        try
        {
            await CreateAdvancedStemsAsync();
        }
        catch (Exception exception)
        {
            RemovalStatus = $"Error al abrir la separación avanzada: {exception.Message}";
            StatusMessage = "No se pudo abrir la separación avanzada";
            MessageBox.Show(
                exception.ToString(),
                "Separación avanzada · error al abrir",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task CreateAdvancedStemsAsync()
    {
        if (IsRemovingDrums)
        {
            ShowAdvancedMessage(
                "Ya hay una separación de audio en curso. Espera a que termine o cancélala antes de iniciar otra.");
            return;
        }

        var sourceTrack = ResolveAdvancedSourceTrack();
        if (sourceTrack is null)
        {
            ShowAdvancedMessage(
                "Carga o selecciona en la biblioteca una pista original. " +
                "Las pistas generadas, los coros o las mezclas anteriores no sirven como origen de una nueva separación avanzada.");
            return;
        }

        if (sourceTrack.IsMissing || !File.Exists(sourceTrack.Path))
        {
            sourceTrack.IsMissing = true;
            RefreshLibraryPresentation();
            SaveTrackWorkspace();
            ShowAdvancedMessage("El archivo original ya no existe en el disco.");
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
        var requiresAdvancedEngine = AdvancedStemMixPlan.RequiresAdvancedSplit(advancedSelection);
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

            if (requiresAdvancedEngine && !_advancedStemSeparation.IsInstalled)
            {
                var answer = MessageBox.Show(
                    "Has elegido conservar solo una parte de las voces o de las guitarras. " +
                    "Para distinguir voz principal/coros o guitarra solista/rítmica se instalará el motor avanzado. " +
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
                exception.ToString(),
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

    private LocalTrack? ResolveAdvancedSourceTrack()
    {
        if (CurrentTrack is { Variant: TrackVariant.Original } current)
        {
            return current;
        }

        return SelectedLibraryTrack is { Variant: TrackVariant.Original } selected
            ? selected
            : null;
    }

    private void ShowAdvancedMessage(string message)
    {
        StatusMessage = message;
        MessageBox.Show(
            message,
            "Separación avanzada",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }
}
