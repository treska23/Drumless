using DrumPracticeStudio.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DrumPracticeStudio.Services;

public static class StemAudioMixer
{
    public static async Task MixAsync(
        string stemRoot,
        StemSelection selection,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stemRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        StemMixPlan.Validate(selection);

        var stemPaths = StemMixPlan.GetFileNames(selection)
            .Select(fileName => Directory
                .EnumerateFiles(stemRoot, fileName, SearchOption.AllDirectories)
                .FirstOrDefault() ?? throw new InvalidDataException(
                    $"Demucs no produjo el stem {fileName} esperado."))
            .ToArray();

        await MixFilesAsync(stemPaths, destination, cancellationToken);
    }

    public static async Task MixFilesAsync(
        IReadOnlyCollection<string> stemPaths,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stemPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (stemPaths.Count == 0)
        {
            throw new ArgumentException(
                "Selecciona al menos un stem para crear el archivo final.",
                nameof(stemPaths));
        }

        foreach (var stemPath in stemPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stemPath);
            ValidateWave(stemPath);
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readers = stemPaths.Select(path => new AudioFileReader(path)).ToArray();
            try
            {
                var format = readers[0].WaveFormat;
                if (readers.Any(reader => !reader.WaveFormat.Equals(format)))
                {
                    throw new InvalidDataException(
                        "Los stems seleccionados no comparten el mismo formato de audio.");
                }

                var mixer = new MixingSampleProvider(readers) { ReadFully = false };
                WaveFileWriter.CreateWaveFile16(destination, mixer);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateWave(destination);
            }
            finally
            {
                foreach (var reader in readers)
                {
                    reader.Dispose();
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        File.Delete(destination);
                    }
                    catch
                    {
                    }
                }
            }
        }, cancellationToken);
    }

    public static void ValidateWave(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length < 44)
        {
            throw new InvalidDataException("El resultado de separación está vacío.");
        }

        using var reader = new WaveFileReader(path);
        if (reader.TotalTime <= TimeSpan.Zero)
        {
            throw new InvalidDataException("El resultado no contiene audio reproducible.");
        }
    }
}
