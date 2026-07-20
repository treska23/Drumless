using System.Diagnostics;
using DrumPracticeStudio.Audio;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class IsolatedVst3EffectProcessorTests
{
    [TestMethod]
    public void DisposeIgnoringErrors_DoesNotPropagateBrokenPipeFailures()
    {
        IsolatedVst3EffectProcessor.DisposeIgnoringErrors(
            new ThrowingDisposable(new IOException("Pipe is broken.")));
    }

    [TestMethod]
    public void DisposeIgnoringErrors_StillDisposesHealthyResources()
    {
        var disposable = new TrackingDisposable();

        IsolatedVst3EffectProcessor.DisposeIgnoringErrors(disposable);

        Assert.IsTrue(disposable.WasDisposed);
    }

    [TestMethod]
    public void AudioBlockWatchdog_OnlyExpiresTheSameLongRunningRequest()
    {
        var started = Stopwatch.Frequency;

        Assert.IsFalse(IsolatedVst3EffectProcessor.HasAudioBlockTimedOut(
            0,
            started + (Stopwatch.Frequency * 30),
            TimeSpan.FromSeconds(12)));
        Assert.IsFalse(IsolatedVst3EffectProcessor.HasAudioBlockTimedOut(
            started,
            started + (Stopwatch.Frequency * 11),
            TimeSpan.FromSeconds(12)));
        Assert.IsTrue(IsolatedVst3EffectProcessor.HasAudioBlockTimedOut(
            started,
            started + (Stopwatch.Frequency * 12),
            TimeSpan.FromSeconds(12)));
    }

    private sealed class ThrowingDisposable(Exception exception) : IDisposable
    {
        public void Dispose() => throw exception;
    }

    private sealed class TrackingDisposable : IDisposable
    {
        public bool WasDisposed { get; private set; }

        public void Dispose() => WasDisposed = true;
    }
}
