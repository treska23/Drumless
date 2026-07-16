using DrumPracticeStudio.Audio;
using DrumPracticeStudio.Models;
using NAudio.Wave;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class TrackTransportProviderTests
{
    private static readonly WaveFormat OutputFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    [TestMethod]
    public async Task NaturalEnd_IsReportedOnceAndCarriesLoadAndRunGenerations()
    {
        using var temporary = new TemporaryDirectory();
        var trackPath = temporary.Combine("short.wav");
        WritePcmWave(trackPath, durationMilliseconds: 5);
        using var transport = new TrackTransportProvider(OutputFormat);

        var loadGeneration = await transport.LoadAsync(trackPath);
        var runGeneration = transport.Play();
        var buffer = new float[2_048];
        transport.Read(buffer);

        Assert.AreEqual(TrackPlaybackState.Ended, transport.PlaybackState);
        Assert.IsFalse(transport.IsPlaying);
        Assert.AreEqual(transport.Duration, transport.Position);
        Assert.IsTrue(transport.TryDequeueTrackEnded(out var completion));
        Assert.AreEqual(loadGeneration, completion.LoadGeneration);
        Assert.AreEqual(runGeneration, completion.RunGeneration);
        Assert.IsFalse(transport.TryDequeueTrackEnded(out _));

        transport.Read(buffer);
        Assert.IsFalse(transport.TryDequeueTrackEnded(out _),
            "El mismo final natural no debe notificarse más de una vez.");
    }

    [TestMethod]
    public async Task PauseAndStop_DoNotReportNaturalEnd()
    {
        using var temporary = new TemporaryDirectory();
        var trackPath = temporary.Combine("track.wav");
        WritePcmWave(trackPath, durationMilliseconds: 100);
        using var transport = new TrackTransportProvider(OutputFormat);
        await transport.LoadAsync(trackPath);
        var buffer = new float[64];

        transport.Play();
        transport.Pause();
        transport.Read(buffer);
        Assert.AreEqual(TrackPlaybackState.Paused, transport.PlaybackState);
        Assert.IsFalse(transport.TryDequeueTrackEnded(out _));

        transport.Play();
        transport.Stop();
        transport.Read(buffer);
        Assert.AreEqual(TrackPlaybackState.Stopped, transport.PlaybackState);
        Assert.AreEqual(TimeSpan.Zero, transport.Position);
        Assert.IsFalse(transport.TryDequeueTrackEnded(out _));
    }

    [TestMethod]
    public async Task Unload_ClearsTheInstalledSessionAndInvalidatesPlayback()
    {
        using var temporary = new TemporaryDirectory();
        var trackPath = temporary.Combine("track.wav");
        WritePcmWave(trackPath, durationMilliseconds: 100);
        using var transport = new TrackTransportProvider(OutputFormat);
        await transport.LoadAsync(trackPath);
        transport.Play();

        transport.Unload();

        Assert.AreEqual(TrackPlaybackState.NoTrack, transport.PlaybackState);
        Assert.AreEqual(TimeSpan.Zero, transport.Position);
        Assert.AreEqual(TimeSpan.Zero, transport.Duration);
        Assert.IsFalse(transport.IsPlaying);
        Assert.IsFalse(transport.TryDequeueTrackEnded(out _));
    }

    [TestMethod]
    public async Task LoadingAnotherTrack_InvalidatesAnOldEndNotification()
    {
        using var temporary = new TemporaryDirectory();
        var firstPath = temporary.Combine("first.wav");
        var secondPath = temporary.Combine("second.wav");
        WritePcmWave(firstPath, durationMilliseconds: 5);
        WritePcmWave(secondPath, durationMilliseconds: 40);
        using var transport = new TrackTransportProvider(OutputFormat);
        var buffer = new float[2_048];

        var firstGeneration = await transport.LoadAsync(firstPath);
        transport.Play();
        transport.Read(buffer);
        var secondGeneration = await transport.LoadAsync(secondPath);

        Assert.IsTrue(secondGeneration > firstGeneration);
        Assert.AreEqual(TrackPlaybackState.Stopped, transport.PlaybackState);
        Assert.IsFalse(transport.TryDequeueTrackEnded(out _));
    }

    [TestMethod]
    public async Task ConcurrentLoads_LeaveTheLatestRequestedTrackActive()
    {
        using var temporary = new TemporaryDirectory();
        var firstPath = temporary.Combine("first.wav");
        var lastPath = temporary.Combine("last.wav");
        WritePcmWave(firstPath, durationMilliseconds: 15);
        WritePcmWave(lastPath, durationMilliseconds: 85);
        var firstReachedInstallBarrier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstInstall = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new TrackTransportProvider(
            OutputFormat,
            async (path, cancellationToken) =>
            {
                if (!string.Equals(path, firstPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                firstReachedInstallBarrier.TrySetResult();
                await releaseFirstInstall.Task.WaitAsync(cancellationToken);
            });

        var firstLoad = transport.LoadAsync(firstPath);
        await firstReachedInstallBarrier.Task;
        var lastLoad = transport.LoadAsync(lastPath);
        var lastGeneration = await lastLoad;
        releaseFirstInstall.TrySetResult();
        await IgnoreSupersededLoadAsync(firstLoad);

        Assert.IsTrue(lastGeneration > 0L);
        Assert.AreEqual(TrackPlaybackState.Stopped, transport.PlaybackState);
        Assert.AreEqual(85d, transport.Duration.TotalMilliseconds, 1.5d);
    }

    [TestMethod]
    public async Task Metronome_IsRenderedAtTheTrackSamplePositionAndFollowsSeek()
    {
        using var temporary = new TemporaryDirectory();
        var trackPath = temporary.Combine("silent.wav");
        WriteSilentPcmWave(trackPath, durationMilliseconds: 1_000);
        using var transport = new TrackTransportProvider(OutputFormat);
        await transport.LoadAsync(trackPath);
        transport.ConfigureMetronome(new TempoSettings(
            120d,
            0.10d,
            MetronomeEnabled: true,
            MetronomeVolume: 0.8d));
        transport.Play();
        var buffer = new float[48_000 * 2 / 4];

        transport.Read(buffer);

        var beforeClick = buffer.AsSpan(0, 48_000 * 2 / 20);
        var aroundClick = buffer.AsSpan(48_000 * 2 / 10, 2_000);
        Assert.AreEqual(0f, beforeClick.ToArray().Max(Math.Abs), 1e-7f);
        Assert.IsTrue(aroundClick.ToArray().Max(Math.Abs) > 0.2f);

        transport.Seek(TimeSpan.FromSeconds(0.60d));
        var seekBuffer = new float[4_000];
        transport.Read(seekBuffer);
        Assert.IsTrue(seekBuffer.Max(Math.Abs) > 0.2f,
            "El pulso en 0,60 s debe conservar la fase después de buscar.");
    }

    [TestMethod]
    public async Task UnloadDuringLoad_PreventsThePreparedTrackFromBeingInstalled()
    {
        using var temporary = new TemporaryDirectory();
        var trackPath = temporary.Combine("pending.wav");
        WritePcmWave(trackPath, durationMilliseconds: 50);
        var reachedInstallBarrier = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInstall = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var transport = new TrackTransportProvider(
            OutputFormat,
            async (_, cancellationToken) =>
            {
                reachedInstallBarrier.TrySetResult();
                await releaseInstall.Task.WaitAsync(cancellationToken);
            });

        var pendingLoad = transport.LoadAsync(trackPath);
        await reachedInstallBarrier.Task;
        transport.Unload();
        releaseInstall.TrySetResult();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await pendingLoad);
        Assert.AreEqual(TrackPlaybackState.NoTrack, transport.PlaybackState);
        Assert.AreEqual(TimeSpan.Zero, transport.Duration);
        Assert.IsFalse(transport.TryDequeueTrackEnded(out _));
    }

    private static async Task IgnoreSupersededLoadAsync(Task<long> load)
    {
        try
        {
            await load;
        }
        catch (OperationCanceledException)
        {
            // Es el resultado esperado cuando una carga posterior gana la carrera.
        }
    }

    private static void WritePcmWave(string path, int durationMilliseconds)
    {
        const int sampleRate = 48_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        var sampleCount = sampleRate * durationMilliseconds / 1_000;
        var blockAlign = (short)(channels * bitsPerSample / 8);
        var dataSize = sampleCount * blockAlign;

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);
        for (var sample = 0; sample < sampleCount; sample++)
        {
            writer.Write((short)(Math.Sin(sample * 2d * Math.PI * 220d / sampleRate) * 4_000d));
        }
    }

    private static void WriteSilentPcmWave(string path, int durationMilliseconds)
    {
        const int sampleRate = 48_000;
        using var writer = new WaveFileWriter(path, new WaveFormat(sampleRate, 16, 1));
        writer.Write(new byte[sampleRate * 2 * durationMilliseconds / 1_000]);
    }
}
