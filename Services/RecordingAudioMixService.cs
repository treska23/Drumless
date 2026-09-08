using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DrumPracticeStudio.Services;

public static class RecordingAudioMixService
{
    private const int TargetSampleRate = 48_000;

    public static async Task RenderAsync(
        IReadOnlyList<string> sourcePaths,
        string destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        if (sourcePaths.Count == 0)
        {
            throw new ArgumentException("Se necesita al menos una fuente de grabación.", nameof(sourcePaths));
        }

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readers = sourcePaths
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .Select(path => new AudioFileReader(path))
                .ToArray();
            if (readers.Length == 0)
            {
                throw new InvalidDataException("No hay archivos de audio válidos para construir la toma.");
            }

            try
            {
                var normalized = readers
                    .Select(reader => Normalize(reader))
                    .ToArray();
                var mixer = new MixingSampleProvider(normalized) { ReadFully = false };
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

    private static ISampleProvider Normalize(ISampleProvider source)
    {
        ISampleProvider normalized = source.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(source),
            2 => source,
            _ => new FirstTwoChannelsSampleProvider(source)
        };

        if (normalized.WaveFormat.SampleRate != TargetSampleRate)
        {
            normalized = new WdlResamplingSampleProvider(normalized, TargetSampleRate);
        }

        return normalized;
    }

    private sealed class FirstTwoChannelsSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _sourceChannels;
        private float[] _scratch = [];

        public FirstTwoChannelsSampleProvider(ISampleProvider source)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _sourceChannels = source.WaveFormat.Channels;
            if (_sourceChannels < 2)
            {
                throw new ArgumentException("La fuente debe tener al menos dos canales.", nameof(source));
            }

            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                source.WaveFormat.SampleRate,
                2);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var requestedFrames = count / 2;
            if (requestedFrames <= 0)
            {
                return 0;
            }

            var requestedInputSamples = requestedFrames * _sourceChannels;
            if (_scratch.Length < requestedInputSamples)
            {
                _scratch = new float[requestedInputSamples];
            }

            var read = _source.Read(_scratch, 0, requestedInputSamples);
            var frames = read / _sourceChannels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sourceOffset = frame * _sourceChannels;
                var destinationOffset = offset + frame * 2;
                buffer[destinationOffset] = _scratch[sourceOffset];
                buffer[destinationOffset + 1] = _scratch[sourceOffset + 1];
            }

            return frames * 2;
        }
    }
}
