using System.Windows;
using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.ViewModels;

public sealed partial class MainViewModel
{
    public void EnablePermanentTrackDeletionPrompt()
    {
        RemoveLibraryTrackCommand = new RelayCommand<LocalTrack>(track =>
        {
            if (track is not null)
            {
                RemoveLibrarySelectionWithDeletePrompt([track]);
            }
        });
    }

    public void RemoveLibrarySelectionWithDeletePrompt(IReadOnlyList<LocalTrack> tracks)
    {
        var selected = tracks
            .DistinctBy(track => track.Id)
            .ToArray();
        if (selected.Length == 0)
        {
            StatusMessage = "Selecciona una o varias pistas de la biblioteca";
            return;
        }

        var itemLabel = selected.Length == 1
            ? $"«{selected[0].Title}»"
            : $"las {selected.Length} pistas seleccionadas";
        var answer = MessageBox.Show(
            $"¿Qué quieres hacer con {itemLabel}?\n\n" +
            "Sí: borrar definitivamente los archivos del disco y quitarlos de la biblioteca.\n" +
            "No: quitarlos solo de la biblioteca y conservar los archivos.\n" +
            "Cancelar: no hacer nada.",
            "Eliminar pistas",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);

        if (answer == MessageBoxResult.Cancel)
        {
            return;
        }
        if (answer == MessageBoxResult.No)
        {
            RemoveLibrarySelection(selected);
            return;
        }

        DeleteTracksFromDiskAndLibrary(selected);
    }

    private void DeleteTracksFromDiskAndLibrary(IReadOnlyList<LocalTrack> tracks)
    {
        var removed = 0;
        var failures = new List<string>();

        foreach (var track in tracks)
        {
            try
            {
                ReleaseTrackFileIfLoaded(track);
                if (File.Exists(track.Path))
                {
                    File.Delete(track.Path);
                }

                if (RemoveTrackFromLibraryCore(track))
                {
                    removed++;
                }
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                ArgumentException)
            {
                failures.Add($"{track.Title}: {exception.Message}");
            }
        }

        if (removed > 0)
        {
            RefreshLibraryPresentation();
            RefreshPerformanceHistory();
            SaveTrackWorkspace();
        }

        if (failures.Count == 0)
        {
            StatusMessage = removed == 1
                ? "1 pista eliminada de la biblioteca y del disco"
                : $"{removed} pistas eliminadas de la biblioteca y del disco";
            return;
        }

        var failureSummary = string.Join(Environment.NewLine, failures.Take(4));
        if (failures.Count > 4)
        {
            failureSummary += $"\n…y {failures.Count - 4} errores más";
        }

        StatusMessage = removed == 0
            ? "No se pudo eliminar ninguna pista del disco"
            : $"{removed} pistas eliminadas; {failures.Count} no se pudieron borrar";
        MessageBox.Show(
            "No se pudieron eliminar estos archivos:\n\n" + failureSummary,
            "Algunos archivos no se eliminaron",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void ReleaseTrackFileIfLoaded(LocalTrack track)
    {
        if (CurrentTrack?.Id != track.Id)
        {
            return;
        }

        StopTrack();
        CancelPendingTrackLoad();
        _audio.UnloadTrack();
        CurrentTrack = null;
    }
}
