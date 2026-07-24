using System.Windows;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    private readonly AdvancedStemSeparationService _advancedStemSeparation = new();
    private RelayCommand? _createAdvancedStemsCommand;

    public RelayCommand CreateAdvancedStemsCommand =>
        _createAdvancedStemsCommand ??= new RelayCommand(() => _ = CreateAdvancedStemsAsync());

    public string AdvancedSeparationLabel =>
        "Voz principal + coros + guitarra solista/rítmica";

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
                    "La separación avanzada necesita primero Demucs para aislar la voz y la guitarra completas. " +
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
                    "Para separar voz principal/coros se instalará Audio Separator 0.44.5 con el ensemble UVR Karaoke. " +
                    "También se instalará el analizador local de guitarra solista/rítmica.\n\n" +
                    "La primera instalación y la primera descarga de modelos pueden ocupar varios GB y tardar bastante en CPU. " +
                    "Todo queda aislado dentro de Drumless.\n\n¿Instalar ahora?",
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
            StatusMessage = $"Separación avanzada de {sourceTrack.Title}…";
            RemovalStatus = "Preparando voz principal, coros y guitarras";

            var result = await _advancedStemSeparation.CreateAsync(
                sourceTrack,
                OutputFolderPath,
                progress,
                cancellationToken);

            _trackLibrary.RegisterGenerated(
                result.LeadVocalPath,
                $"{sourceTrack.Title} · voz principal");
            _trackLibrary.RegisterGenerated(
                result.BackVocalPath,
                $"{sourceTrack.Title} · coros");
            _trackLibrary.RegisterGenerated(
                result.LeadGuitarPath,
                $"{sourceTrack.Title} · guitarra solista · experimental");
            _trackLibrary.RegisterGenerated(
                result.RhythmGuitarPath,
                $"{sourceTrack.Title} · guitarra rítmica · experimental");

            SaveTrackWorkspace();
            RefreshLibraryPresentation();
            RemovalProgress = 1d;
            RemovalStatus = "4 stems avanzados creados y añadidos a la biblioteca";
            StatusMessage = "Listas: voz principal, coros, guitarra solista y guitarra rítmica";
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
