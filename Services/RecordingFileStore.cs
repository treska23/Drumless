namespace DrumPracticeStudio.Services;

internal static class RecordingFileStore
{
    public static string Publish(string renderedPath, string requestedDestination)
    {
        var destination = Path.GetFullPath(requestedDestination);
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var stagingPath = Path.Combine(directory, $".toma-{Guid.NewGuid():N}.tmp");
        try
        {
            // Stage on the destination volume so publishing is atomic even when
            // the recording work directory is on a different disk.
            File.Copy(renderedPath, stagingPath, overwrite: false);
            var candidate = destination;
            var suffix = 2;
            while (true)
            {
                try
                {
                    File.Move(stagingPath, candidate, overwrite: false);
                    return candidate;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    candidate = Path.Combine(directory,
                        $"{Path.GetFileNameWithoutExtension(destination)}-{suffix++}{Path.GetExtension(destination)}");
                }
            }
        }
        finally
        {
            try { File.Delete(stagingPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public static void WriteRecoveryNote(string? workDirectory, string? destination, Exception error)
    {
        if (string.IsNullOrWhiteSpace(workDirectory) || !Directory.Exists(workDirectory)) return;
        try
        {
            File.WriteAllText(Path.Combine(workDirectory, "RECUPERAR_TOMA.txt"),
                $"Toma sin finalizar · {DateTimeOffset.Now:O}{Environment.NewLine}" +
                $"Destino solicitado: {destination}{Environment.NewLine}" +
                $"Error: {error.Message}{Environment.NewLine}{Environment.NewLine}" +
                "Se han conservado los WAV de esta carpeta para recuperar la grabación. " +
                "Si existe un archivo final-*.wav, contiene la mezcla terminada. " +
                "Los archivos endpoint-*.wav contienen la salida de Windows; " +
                "internal.wav contiene la mezcla de Drumless cuando se usa ASIO.");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
