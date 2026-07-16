using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class RecordingSampleProvider(
    ISampleProvider source,
    OutputRecordingSink sink) : ISampleProvider
{
    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(Span<float> buffer)
    {
        var read = source.Read(buffer);
        sink.Capture(buffer[..read]);
        return read;
    }
}
