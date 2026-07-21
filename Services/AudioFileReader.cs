using NAudio.Wave;

namespace DrumPracticeStudio.Services;

/// <summary>
/// Lector de audio común para los servicios de Drumless.
///
/// Esta clase existe deliberadamente en el namespace Services para que las rutas de mezcla de stems,
/// análisis, tempo y separación no resuelvan por accidente NAudio.Wave.AudioFileReader. En NAudio 3
/// preview, ese lector genérico intenta localizar Media Foundation dinámicamente y puede lanzar el
/// mensaje "Media Foundation reader requires the NAudio.Wasapi package" incluso cuando el paquete
/// está referenciado.
///
/// Delegamos en DrumPracticeStudio.Audio.AudioFileReader, que selecciona el decoder explícitamente y
/// permite abrir FLAC (además de WAV, MP3, AIFF y los formatos disponibles mediante Media Foundation).
/// </summary>
internal sealed class AudioFileReader : ISampleProvider, IDisposable
{
    private readonly DrumPracticeStudio.Audio.AudioFileReader _inner;

    public AudioFileReader(string path)
    {
        _inner = new DrumPracticeStudio.Audio.AudioFileReader(path);
    }

    public WaveFormat WaveFormat => _inner.WaveFormat;
    public long Length => _inner.Length;

    public long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public TimeSpan TotalTime => _inner.TotalTime;

    public TimeSpan CurrentTime
    {
        get => _inner.CurrentTime;
        set => _inner.CurrentTime = value;
    }

    public int Read(Span<float> buffer) => _inner.Read(buffer);

    public void Dispose() => _inner.Dispose();
}
