using DrumPracticeStudio.Audio;
using NAudio.Wave;
using DrumlessAudioFileReader = DrumPracticeStudio.Audio.AudioFileReader;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class OutputRecordingSinkTests
{
    [TestMethod]
    public async Task StopAsync_WritesEveryCapturedStereoSampleToAPlayableWave()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("take.wav");
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        using var sink = new OutputRecordingSink();
        sink.Start(path, format);
        var expected = Enumerable.Range(0, 960)
            .Select(index => (float)Math.Sin(index * 0.02d) * 0.25f)
            .ToArray();

        sink.Capture(expected.AsSpan(0, 300));
        sink.Capture(expected.AsSpan(300));
        var result = await sink.StopAsync();

        Assert.AreEqual(path, result);
        using var reader = new DrumlessAudioFileReader(path);
        var actual = new float[expected.Length];
        var read = ((ISampleProvider)reader).Read(actual.AsSpan());
        Assert.AreEqual(expected.Length, read);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.AreEqual(expected[index], actual[index], 1e-6f);
        }
    }

    [TestMethod]
    public async Task CaptureDuringStop_DoesNotLeakSamplesIntoTheNextTake()
    {
        using var temporary = new TemporaryDirectory();
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        using var sink = new OutputRecordingSink();
        var previousSamples = Enumerable.Repeat(0.25f, 960).ToArray();
        var nextSamples = Enumerable.Repeat(-0.5f, 480).ToArray();

        for (var iteration = 0; iteration < 20; iteration++)
        {
            sink.Start(temporary.Combine($"previous-{iteration}.wav"), format);
            using var capturing = new ManualResetEventSlim();
            using var stopCapture = new CancellationTokenSource();
            var capture = Task.Run(() =>
            {
                sink.Capture(previousSamples);
                capturing.Set();
                while (!stopCapture.IsCancellationRequested)
                {
                    sink.Capture(previousSamples);
                    Thread.Yield();
                }
            });
            try
            {
                Assert.IsTrue(capturing.Wait(TimeSpan.FromSeconds(5)));
                await sink.StopAsync();
            }
            finally
            {
                stopCapture.Cancel();
                await capture;
            }

            var nextPath = temporary.Combine($"next-{iteration}.wav");
            sink.Start(nextPath, format);
            sink.Capture(nextSamples);
            await sink.StopAsync();

            using var reader = new DrumlessAudioFileReader(nextPath);
            var recorded = new float[nextSamples.Length + previousSamples.Length];
            var read = ((ISampleProvider)reader).Read(recorded);
            Assert.AreEqual(nextSamples.Length, read,
                "La nueva toma solo debe contener sus propias muestras.");
            CollectionAssert.AreEqual(nextSamples, recorded[..read]);
        }
    }

    [TestMethod]
    public async Task RecordingSampleProvider_RecordsExactlyWhatItReturns()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("provider.wav");
        var source = new ConstantSampleProvider(0.35f);
        using var sink = new OutputRecordingSink();
        sink.Start(path, source.WaveFormat);
        var provider = new RecordingSampleProvider(source, sink);
        var rendered = new float[480];

        provider.Read(rendered);
        await sink.StopAsync();

        using var reader = new DrumlessAudioFileReader(path);
        var recorded = new float[rendered.Length];
        ((ISampleProvider)reader).Read(recorded);
        CollectionAssert.AreEqual(rendered, recorded);
    }

    private sealed class ConstantSampleProvider(float value) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } =
            WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

        public int Read(Span<float> buffer)
        {
            buffer.Fill(value);
            return buffer.Length;
        }
    }
}
