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
    private bool _shellControllerRecoverySucceeded;
    private string _shellControllerRecoveryDiagnostic = "no ejecutada";

    private IsolatedVst3EffectProcessor(InProcessVst3EffectCore core)
    {
        _core = core;

        if (LooksLikeWaveShell(_core.Reference) && TryGetCoreModule() is { } module)
        {
            _shellControllerRecoveryAttempted = true;

            // Steinberg's host guidance says that when there is no separate edit-controller class,
            // the host must also query the audio processor for IEditController. The generic host only
            // queried the component before initialize(), which misses some shell-style plug-ins.
            // Try the live processor/component COM identities first; this is the closest match to the
            // single-component compatibility path described by the VST3 SDK.
            _shellControllerRecoverySucceeded = Vst3SameObjectControllerRecovery.TryRecover(
                _core.Plugin,
                out _shellControllerRecoveryDiagnostic);

            if (!_shellControllerRecoverySucceeded)
            {
                // Secondary compatibility path: probe a fresh component while it is still in Created
                // state and ask for a separate controller CID before initialize().
                var probeSucceeded = Vst3WaveShellControllerProbe.TryRecover(
                    _core.Plugin,
                    module,
                    out var probeDiagnostic);
                _shellControllerRecoveryDiagnostic += $"; probe Created: {probeDiagnostic}";
                _shellControllerRecoverySucceeded = probeSucceeded;
            }

            if (!_shellControllerRecoverySucceeded)
            {
                // Final generic fallback retained for non-standard factories.
                var genericSucceeded = Vst3ControllerRecovery.TryRecoverForEditor(
                    _core.Plugin,
                    module,
                    replaceExistingController: true);
                if (genericSucceeded)
                {
                    _shellControllerRecoverySucceeded = true;
                    _shellControllerRecoveryDiagnostic += "; fallback genérico correcto";
                }
                else
                {
                    _shellControllerRecoveryDiagnostic += "; fallback genérico fallido";
                }
            }
        }

        _core.ApplyConfiguredParameters();
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

    public async Task<Vst3EffectEditorResult> OpenEditorAsync(
        CancellationToken cancellationToken = default)
    {
        // Non-WaveShell plug-ins keep the normal late fallback in case they genuinely loaded
        // without an edit controller. Guitar Rig already works through this normal path.
        if (!_shellControllerRecoveryAttempted &&
            !_core.Plugin.HasEditController &&
            TryGetCoreModule() is { } fallbackModule)
        {
            _shellControllerRecoveryAttempted = true;
            _shellControllerRecoverySucceeded = Vst3ControllerRecovery.TryRecoverForEditor(
                _core.Plugin,
                fallbackModule);
            _shellControllerRecoveryDiagnostic = _shellControllerRecoverySucceeded
                ? "fallback genérico correcto"
                : "fallback genérico fallido";
        }

        var result = await _core.OpenEditorAsync(cancellationToken);
        if (!result.Succeeded && LooksLikeWaveShell(Reference))
        {
            var state = _shellControllerRecoveryAttempted
                ? (_shellControllerRecoverySucceeded ? "correcta" : "fallida")
                : "no ejecutada";
            return result with
            {
                Message =
                    $"{result.Message} [Recuperación WaveShell: {state}. " +
                    $"Detalle: {_shellControllerRecoveryDiagnostic}]"
            };
        }

        return result;
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
