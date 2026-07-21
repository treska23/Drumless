using System.Security.Cryptography;
using System.Text;
using System.Windows;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using DrumPracticeStudio.Views;
using NAudio.Vst3;

namespace DrumPracticeStudio.Audio;

/// <summary>
/// Realtime in-process VST3 effect host. It avoids the asynchronous NamedPipe round-trip that was
/// previously performed for every audio block and that could not reliably keep pace with low-latency
/// ASIO callbacks.
/// </summary>
internal sealed class InProcessVst3EffectCore : IDisposable
{
    private readonly object _gate = new();
    private readonly Vst3Module _module;
    private readonly Vst3PluginView? _view;
    private readonly string _statePath;
    private readonly float[] _pluginInput;
    private readonly float[] _pluginOutput;
    private Vst3EditorWindow? _editor;
    private string? _failure;
    private bool _disposed;

    private InProcessVst3EffectCore(
        Vst3Module module,
        Vst3Plugin plugin,
        Vst3PluginView? view,
        Vst3EffectReference reference,
        string statePath)
    {
        _module = module;
        Plugin = plugin;
        _view = view;
        Reference = reference;
        _statePath = statePath;
        _pluginInput = new float[Math.Max(1, AudioLatencySettings.VstMaxBlockSize) * Math.Max(1, plugin.InputChannelCount)];
        _pluginOutput = new float[Math.Max(1, AudioLatencySettings.VstMaxBlockSize) * Math.Max(1, plugin.OutputChannelCount)];
    }

    public Vst3Plugin Plugin { get; }
    public Vst3EffectReference Reference { get; }
    public uint TotalLatencySamples => Plugin.LatencySamples;
    public string? Failure => Volatile.Read(ref _failure);
    public bool IsAvailable => !_disposed && Failure is null;
    public bool HasEditor => _view is not null;

    public static InProcessVst3EffectCore Start(
        Vst3EffectReference reference,
        int sampleRate,
        string slotId)
    {
        ArgumentNullException.ThrowIfNull(reference);

        Vst3Module? module = null;
        Vst3Plugin? plugin = null;
        Vst3PluginView? view = null;
        try
        {
            module = Vst3Module.Load(reference.ModulePath);
            var pluginClass = new Vst3ClassInfo(
                reference.ClassId,
                reference.Category,
                reference.Name,
                reference.Vendor,
                reference.Version,
                reference.SdkVersion,
                reference.SubCategories);
            plugin = module.CreatePlugin(pluginClass, sampleRate, AudioLatencySettings.VstMaxBlockSize);
            if (plugin.IsInstrument)
            {
                throw new InvalidOperationException($"{reference.Name} es un instrumento, no un efecto.");
            }
            if (plugin.InputChannelCount is not (1 or 2) || plugin.OutputChannelCount is not (1 or 2))
            {
                throw new NotSupportedException(
                    $"{reference.Name} expone {plugin.InputChannelCount} entrada(s) y " +
                    $"{plugin.OutputChannelCount} salida(s); sólo se admiten buses mono o estéreo.");
            }

            var statePath = GetAutomaticStatePath(slotId, reference);
            var loadPath = File.Exists(statePath) ? statePath : reference.PresetPath;

            // A VST3 processor may be perfectly usable for audio while exposing no edit controller.
            // The vendor host currently reports that situation as ObjectDisposedException when
            // LoadPreset is attempted. State restore is therefore best-effort and must never prevent
            // the DSP from loading. This is especially important for shell-style Waves plug-ins.
            if (plugin.HasEditController &&
                !string.IsNullOrWhiteSpace(loadPath) &&
                File.Exists(loadPath))
            {
                try
                {
                    plugin.LoadPreset(loadPath);
                }
                catch (Exception exception) when (exception is
                    IOException or
                    UnauthorizedAccessException or
                    InvalidDataException or
                    InvalidOperationException or
                    ObjectDisposedException or
                    NotSupportedException)
                {
                    // An old, corrupt or host-incompatible state must not put a working effect in bypass.
                }
            }

            try
            {
                view = plugin.CreateView();
            }
            catch
            {
                // Some effects process correctly even though their native editor cannot be embedded.
            }

            return new InProcessVst3EffectCore(module, plugin, view, reference, statePath);
        }
        catch
        {
            view?.Dispose();
            plugin?.Dispose();
            module?.Dispose();
            throw;
        }
    }

    public void ProcessMono(Span<float> samples, float wetMix)
    {
        if (samples.IsEmpty || !IsAvailable)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _failure is not null)
            {
                return;
            }

            try
            {
                var mix = Math.Clamp(wetMix, 0f, 1f);
                var dryMix = 1f - mix;
                var offset = 0;
                while (offset < samples.Length)
                {
                    var frames = Math.Min(AudioLatencySettings.VstMaxBlockSize, samples.Length - offset);
                    FillMonoInput(samples.Slice(offset, frames), frames);
                    ProcessPlugin(frames);
                    MixMonoOutput(samples.Slice(offset, frames), frames, dryMix, mix);
                    offset += frames;
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }
    }

    public void ProcessStereo(Span<float> interleaved, float wetMix)
    {
        var totalFrames = interleaved.Length / 2;
        if (totalFrames == 0 || !IsAvailable)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _failure is not null)
            {
                return;
            }

            try
            {
                var mix = Math.Clamp(wetMix, 0f, 1f);
                var dryMix = 1f - mix;
                var frameOffset = 0;
                while (frameOffset < totalFrames)
                {
                    var frames = Math.Min(AudioLatencySettings.VstMaxBlockSize, totalFrames - frameOffset);
                    FillStereoInput(interleaved, frameOffset, frames);
                    ProcessPlugin(frames);
                    MixStereoOutput(interleaved, frameOffset, frames, dryMix, mix);
                    frameOffset += frames;
                }
            }
            catch (Exception exception)
            {
                Fail(exception);
            }
        }
    }

    public Task<Vst3EffectEditorResult> OpenEditorAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return Task.FromResult(new Vst3EffectEditorResult(false, Failure ?? $"{Reference.Name} no está activo."));
        }
        if (_view is null)
        {
            return Task.FromResult(new Vst3EffectEditorResult(
                false,
                $"{Reference.Name} no proporciona una interfaz VST3 compatible."));
        }

        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_editor is not null)
            {
                _editor.Activate();
                return new Vst3EffectEditorResult(true, $"La interfaz de {Reference.Name} ya estaba abierta.");
            }

            _editor = new Vst3EditorWindow(Reference.Name, _view)
            {
                ShowInTaskbar = true
            };
            _editor.ClosedByUser += (_, _) =>
            {
                _editor = null;
                SaveState();
            };
            _editor.Show();
            _editor.Activate();
            return new Vst3EffectEditorResult(true, $"Interfaz de {Reference.Name} abierta.");
        }).Task;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                SaveState();
            }
            catch
            {
            }
            _disposed = true;
            try
            {
                _editor?.Close();
            }
            catch
            {
            }
            _editor = null;
            _view?.Dispose();
            Plugin.Dispose();
            _module.Dispose();
        }
    }

    private void ProcessPlugin(int frames)
    {
        Plugin.Process(
            _pluginInput.AsSpan(0, frames * Plugin.InputChannelCount),
            _pluginOutput.AsSpan(0, frames * Plugin.OutputChannelCount),
            frames);
    }

    private void FillMonoInput(ReadOnlySpan<float> mono, int frames)
    {
        if (Plugin.InputChannelCount == 1)
        {
            mono.CopyTo(_pluginInput);
            return;
        }
        for (var frame = 0; frame < frames; frame++)
        {
            var sample = mono[frame];
            _pluginInput[frame * 2] = sample;
            _pluginInput[(frame * 2) + 1] = sample;
        }
    }

    private void FillStereoInput(ReadOnlySpan<float> stereo, int frameOffset, int frames)
    {
        if (Plugin.InputChannelCount == 1)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                var source = (frameOffset + frame) * 2;
                _pluginInput[frame] = (stereo[source] + stereo[source + 1]) * 0.5f;
            }
            return;
        }
        stereo.Slice(frameOffset * 2, frames * 2).CopyTo(_pluginInput);
    }

    private void MixMonoOutput(Span<float> mono, int frames, float dryMix, float wetMix)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            var wet = Plugin.OutputChannelCount == 1
                ? _pluginOutput[frame]
                : (_pluginOutput[frame * 2] + _pluginOutput[(frame * 2) + 1]) * 0.5f;
            ValidateSample(wet);
            mono[frame] = Math.Clamp((mono[frame] * dryMix) + (wet * wetMix), -1f, 1f);
        }
    }

    private void MixStereoOutput(Span<float> stereo, int frameOffset, int frames, float dryMix, float wetMix)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            var destination = (frameOffset + frame) * 2;
            if (Plugin.OutputChannelCount == 1)
            {
                var wet = _pluginOutput[frame];
                ValidateSample(wet);
                stereo[destination] = Math.Clamp((stereo[destination] * dryMix) + (wet * wetMix), -1f, 1f);
                stereo[destination + 1] = Math.Clamp((stereo[destination + 1] * dryMix) + (wet * wetMix), -1f, 1f);
            }
            else
            {
                var left = _pluginOutput[frame * 2];
                var right = _pluginOutput[(frame * 2) + 1];
                ValidateSample(left);
                ValidateSample(right);
                stereo[destination] = Math.Clamp((stereo[destination] * dryMix) + (left * wetMix), -1f, 1f);
                stereo[destination + 1] = Math.Clamp((stereo[destination + 1] * dryMix) + (right * wetMix), -1f, 1f);
            }
        }
    }

    private static void ValidateSample(float sample)
    {
        if (!float.IsFinite(sample) || MathF.Abs(sample) > 16f)
        {
            throw new InvalidDataException("El plugin produjo audio no válido o fuera de rango.");
        }
    }

    private void Fail(Exception exception) =>
        Volatile.Write(
            ref _failure,
            $"{Reference.Name} quedó en bypass: {exception.GetType().Name}: {exception.Message}");

    private void SaveState()
    {
        try
        {
            if (!Plugin.HasEditController)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            Plugin.SavePreset(_statePath);
        }
        catch
        {
            // State persistence is optional.
        }
    }

    private static string GetAutomaticStatePath(string slotId, Vst3EffectReference reference)
    {
        var identity = Encoding.UTF8.GetBytes(
            $"{reference.ModulePath}|{reference.ClassId}|{reference.PresetPath}");
        var fingerprint = Convert.ToHexString(SHA256.HashData(identity))[..16];
        var safeSlotId = string.Concat(slotId.Where(char.IsLetterOrDigit));
        if (string.IsNullOrWhiteSpace(safeSlotId))
        {
            safeSlotId = "slot";
        }
        else if (safeSlotId.Length > 64)
        {
            safeSlotId = safeSlotId[..64];
        }
        return Path.Combine(AppPaths.VstStates, $"effect-{safeSlotId}-{fingerprint}.vstpreset");
    }
}