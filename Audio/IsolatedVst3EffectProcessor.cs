using System.Diagnostics;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Audio;

/// <summary>
/// Compatibility facade kept so the rest of the audio graph does not need to change.
/// Effects are now processed directly in the realtime host instead of sending every audio block
/// through a NamedPipe to a secondary process. The old bridge could not reliably keep pace with
/// low-latency ASIO buffers and caused third-party effects to remain dry or fall into bypass.
/// </summary>
internal sealed class IsolatedVst3EffectProcessor : IDisposable
{
    private readonly InProcessVst3EffectCore _core;

    private IsolatedVst3EffectProcessor(InProcessVst3EffectCore core)
    {
        _core = core;
    }

    public Vst3EffectReference Reference => _core.Reference;
    public uint PluginLatencySamples => _core.TotalLatencySamples;
    public uint TotalLatencySamples => _core.TotalLatencySamples;
    public string? Failure => _core.Failure;
    public bool IsAvailable => _core.IsAvailable;
    public bool HasEditor => _core.HasEditor;

    public static IsolatedVst3EffectProcessor Start(
        Vst3EffectReference reference,
        int sampleRate,
        string slotId) =>
        new(InProcessVst3EffectCore.Start(reference, sampleRate, slotId));

    public void ProcessMono(Span<float> samples, float wetMix) =>
        _core.ProcessMono(samples, wetMix);

    public void ProcessStereo(Span<float> samples, float wetMix) =>
        _core.ProcessStereo(samples, wetMix);

    public Task<Vst3EffectEditorResult> OpenEditorAsync(
        CancellationToken cancellationToken = default) =>
        _core.OpenEditorAsync(cancellationToken);

    public void Dispose() => _core.Dispose();

    // Kept for the existing regression tests and for callers that dispose best-effort resources.
    internal static void DisposeIgnoringErrors(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch (Exception exception) when (exception is
            IOException or
            ObjectDisposedException or
            InvalidOperationException)
        {
        }
    }

    // Kept as a pure helper because the existing test suite validates the former watchdog logic.
    // It no longer drives effect processing now that there is no cross-process audio bridge.
    internal static bool HasAudioBlockTimedOut(
        long startedTimestamp,
        long currentTimestamp,
        TimeSpan timeout) =>
        startedTimestamp > 0 &&
        currentTimestamp >= startedTimestamp &&
        Stopwatch.GetElapsedTime(startedTimestamp, currentTimestamp) >= timeout;
}

internal sealed record Vst3EffectEditorResult(bool Succeeded, string Message);
