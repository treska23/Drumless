using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DrumPracticeStudio.Services;

public static class AudioFileMixService
{
    public static async Task MixAsync(
        IReadOnlyList<string> sourcePaths,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("Se necesita al menos una fuente de audio.", nameof(sourcePaths));
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readers = sourcePaths.Select(path => new AudioFileReader(path)).ToArray();
            try
            {
                var format = readers[0].WaveFormat;
                if (readers.Any(reader => !reader.WaveFormat.Equals(format)))
                {
                    throw new InvalidDataException("Las grabaciones no comparten formato de audio.");
                }

                var mixer = new MixingSampleProvider(readers) { ReadFully = false };
                WaveFileWriter.CreateWaveFile16(destination, mixer);
                StemAudioMixer.ValidateWave(destination);
            }
            finally
            {
                foreach (var reader in readers)
                {
                    reader.Dispose();
                }
            }
        }, cancellationToken);
    }
}
