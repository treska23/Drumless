using System.Diagnostics;
using System.Reflection;
using DrumPracticeStudio.Models;
using NAudio.Vst3;

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
    private bool _shellControllerRecoveryAttempted;

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
        CancellationToken cancellationToken = default)
    {
        if (!_shellControllerRecoveryAttempted &&
            LooksLikeWaveShell(Reference) &&
            TryGetCoreModule() is { } module)
        {
            _shellControllerRecoveryAttempted = true;

            // Some WaveShell versions leave a non-null but wrong controller in the generic fallback:
            // it passes QI(IEditController), yet createView("editor") returns null. Replace that
            // controller once with the actual non-zero CID advertised by the component. Guitar Rig
            // and normal VST3s never enter this path, so their already-working editor flow is untouched.
            _ = Vst3ControllerRecovery.TryRecoverForEditor(
                _core.Plugin,
                module,
                replaceExistingController: true);
        }
        else if (!_core.Plugin.HasEditController && TryGetCoreModule() is { } fallbackModule)
        {
            _ = Vst3ControllerRecovery.TryRecoverForEditor(_core.Plugin, fallbackModule);
        }

        return _core.OpenEditorAsync(cancellationToken);
    }

    public void Dispose() => _core.Dispose();

    private static bool LooksLikeWaveShell(Vst3EffectReference reference) =>
        reference.Vendor.Contains("Waves", StringComparison.OrdinalIgnoreCase) ||
        reference.ModulePath.Contains("WaveShell", StringComparison.OrdinalIgnoreCase) ||
        reference.Name.StartsWith("GTR ", StringComparison.OrdinalIgnoreCase);

    private Vst3Module? TryGetCoreModule() =>
        typeof(InProcessVst3EffectCore)
            .GetField("_module", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(_core) as Vst3Module;

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
