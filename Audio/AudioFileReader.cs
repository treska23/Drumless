using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

/// <summary>
/// Lector de audio de la aplicación. Evita la resolución opcional por reflexión que hace
/// NAudio.AudioFileReader para Media Foundation en NAudio 3 preview y referencia directamente
/// MediaFoundationReader, garantizando que NAudio.Wasapi forme parte de la ruta de ejecución.
/// </summary>
internal sealed class AudioFileReader : ISampleProvider, IDisposable
{
    private readonly WaveStream _reader;
    private readonly ISampleProvider _sampleProvider;

    public AudioFileReader(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("No se encontró el archivo de audio.", path);
        }

        _reader = OpenReader(path);
        _sampleProvider = _reader.ToSampleProvider();
    }

    public WaveFormat WaveFormat => _sampleProvider.WaveFormat;
    public long Length => _reader.Length;

    public long Position
    {
        get => _reader.Position;
        set => _reader.Position = Math.Clamp(value, 0, _reader.Length);
    }

    public TimeSpan TotalTime => _reader.TotalTime;

    public TimeSpan CurrentTime
    {
        get => _reader.CurrentTime;
        set => _reader.CurrentTime = value < TimeSpan.Zero
            ? TimeSpan.Zero
            : value > _reader.TotalTime
                ? _reader.TotalTime
                : value;
    }

    public int Read(Span<float> buffer) => _sampleProvider.Read(buffer);

    public void Dispose() => _reader.Dispose();

    private static WaveStream OpenReader(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension switch
        {
            ".wav" => new WaveFileReader(path),
            ".mp3" => new Mp3FileReader(path),
            ".aif" or ".aiff" => new AiffFileReader(path),
            // M4A/AAC/WMA/FLAC y otros formatos admitidos por Media Foundation.
            // La referencia directa evita el Type.GetType dinámico de AudioFileReader,
            // que en NAudio 3 preview puede informar erróneamente que falta NAudio.Wasapi.
            _ => new MediaFoundationReader(path)
        };
    }
}
