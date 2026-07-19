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
