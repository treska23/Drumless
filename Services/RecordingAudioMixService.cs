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

        var resolvedDestination = Path.GetFullPath(destination);
        var sources = sourcePaths.Select(path =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var resolved = Path.GetFullPath(path);
            if (string.Equals(resolved, resolvedDestination, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("El destino no puede reemplazar una fuente de la toma.", nameof(destination));
            }
            return resolved;
        }).ToArray();

        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var readers = new List<AudioFileReader>();
            var temporaryPath = resolvedDestination + $".{Guid.NewGuid():N}.tmp.wav";
            try
            {
                // Every requested source is required. Keep ownership as each reader
                // opens so a later invalid source cannot leave earlier files locked.
                foreach (var source in sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    readers.Add(new AudioFileReader(source));
                }

                var normalized = readers
                    .Select(reader => Normalize(reader))
                    .ToArray();
                var mixer = new MixingSampleProvider(normalized) { ReadFully = false };
                WaveFileWriter.CreateWaveFile16(
                    temporaryPath,
                    new CancellableSampleProvider(mixer, cancellationToken));
                cancellationToken.ThrowIfCancellationRequested();
                StemAudioMixer.ValidateWave(temporaryPath);
                File.Move(temporaryPath, resolvedDestination, overwrite: true);
            }
            catch (Exception exception) when (exception is FormatException or NotSupportedException)
            {
                throw new InvalidDataException("Una fuente de la toma tiene un formato de audio no válido.", exception);
            }
            finally
            {
                foreach (var reader in readers)
                {
                    reader.Dispose();
                }
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original render failure; sources remain available for recovery.
                }
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private sealed class CancellableSampleProvider(
        ISampleProvider source,
        CancellationToken cancellationToken) : ISampleProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return source.Read(buffer);
        }
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

        public int Read(Span<float> buffer)
        {
            var requestedFrames = buffer.Length / 2;
            if (requestedFrames <= 0)
            {
                return 0;
            }

            var requestedInputSamples = requestedFrames * _sourceChannels;
            if (_scratch.Length < requestedInputSamples)
            {
                _scratch = new float[requestedInputSamples];
            }

            var read = _source.Read(_scratch.AsSpan(0, requestedInputSamples));
            var frames = read / _sourceChannels;
            for (var frame = 0; frame < frames; frame++)
            {
                var sourceOffset = frame * _sourceChannels;
                var destinationOffset = frame * 2;
                buffer[destinationOffset] = _scratch[sourceOffset];
                buffer[destinationOffset + 1] = _scratch[sourceOffset + 1];
            }

            return frames * 2;
        }

        public int Read(float[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));
    }
}
