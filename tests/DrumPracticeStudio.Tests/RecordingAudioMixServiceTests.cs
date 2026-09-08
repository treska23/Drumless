using DrumPracticeStudio.Services;
using NAudio.Wave;
using DrumlessAudioFileReader = DrumPracticeStudio.Services.AudioFileReader;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class RecordingAudioMixServiceTests
{
    [TestMethod]
    public async Task RenderAsync_NormalizesMonoAndStereoAndKeepsTheLongerSource()
    {
        using var temporary = new TemporaryDirectory();
        var mono = temporary.Combine("instrument.wav");
        var stereo = temporary.Combine("endpoint.wav");
        var destination = temporary.Combine("take.wav");
        WriteConstant(mono, 24_000, 1, 100, 0.125f);
        WriteConstant(stereo, 48_000, 2, 200, 0.25f);

        await RecordingAudioMixService.RenderAsync([mono, stereo], destination);

        using var reader = new DrumlessAudioFileReader(destination);
        Assert.AreEqual(48_000, reader.WaveFormat.SampleRate);
        Assert.AreEqual(2, reader.WaveFormat.Channels);
        Assert.AreEqual(200d, reader.TotalTime.TotalMilliseconds, 1d);
        var samples = new float[19_200];
        Assert.AreEqual(samples.Length, reader.Read(samples));
        Assert.AreEqual(0.375f, samples[4_800], 0.001f);
        Assert.AreEqual(0.375f, samples[4_801], 0.001f);
        Assert.AreEqual(0.25f, samples[14_400], 0.001f);
    }

    [TestMethod]
    public async Task RenderAsync_MissingRequiredSourceDoesNotOverwriteAnExistingTake()
    {
        using var temporary = new TemporaryDirectory();
        var available = temporary.Combine("endpoint.wav");
        var missing = temporary.Combine("instrument.wav");
        var destination = temporary.Combine("take.wav");
        WriteConstant(available, 48_000, 2, 100, 0.25f);
        WriteConstant(destination, 48_000, 2, 100, 0.5f);
        var original = File.ReadAllBytes(destination);

        await Assert.ThrowsExactlyAsync<FileNotFoundException>(async () =>
            await RecordingAudioMixService.RenderAsync([available, missing], destination));

        CollectionAssert.AreEqual(original, File.ReadAllBytes(destination));
        using var exclusive = File.Open(available, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.AreEqual(0, Directory.GetFiles(temporary.Path, "*.tmp.wav").Length);
    }

    [TestMethod]
    public async Task RenderAsync_InvalidSourceReleasesEveryEarlierReader()
    {
        using var temporary = new TemporaryDirectory();
        var available = temporary.Combine("endpoint.wav");
        var invalid = temporary.Combine("invalid.wav");
        var destination = temporary.Combine("take.wav");
        WriteConstant(available, 48_000, 2, 100, 0.25f);
        File.WriteAllText(invalid, "This is not a WAV recording.");

        Exception? failure = null;
        try
        {
            await RecordingAudioMixService.RenderAsync([available, invalid], destination);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        Assert.IsNotNull(failure, "Una fuente dañada debe impedir publicar una toma incompleta.");
        using var exclusive = File.Open(available, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.IsFalse(File.Exists(destination));
    }

    [TestMethod]
    public async Task RenderAsync_DoesNotPostBackToTheCallingSynchronizationContext()
    {
        using var temporary = new TemporaryDirectory();
        var source = temporary.Combine("endpoint.wav");
        var destination = temporary.Combine("take.wav");
        WriteConstant(source, 48_000, 2, 1_000, 0.25f);
        var context = new UnpumpedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        Task rendering;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            rendering = RecordingAudioMixService.RenderAsync([source], destination);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await rendering.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, context.PostCount,
            "Finalizar una toma durante el cierre no debe depender del dispatcher de la ventana.");
    }

    private static void WriteConstant(
        string path, int sampleRate, int channels, int milliseconds, float value)
    {
        using var writer = new WaveFileWriter(path,
            WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels));
        var samples = Enumerable.Repeat(value, sampleRate * channels * milliseconds / 1_000).ToArray();
        writer.WriteSamples(samples, 0, samples.Length);
    }

    private sealed class UnpumpedSynchronizationContext : SynchronizationContext
    {
        private int _postCount;
        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state) =>
            Interlocked.Increment(ref _postCount);
    }
}
