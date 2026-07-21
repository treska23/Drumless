using System.Security.Cryptography;
using System.Text;
using System.Windows;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using DrumPracticeStudio.Views;
using NAudio.Vst3;

namespace DrumPracticeStudio.Audio;

/// <summary>
/// Hosts a user VST3 effect in-process for the live ASIO input path.
/// The old isolated NamedPipe bridge adds at least one asynchronous audio block and can fall behind
/// the ASIO callback at small buffer sizes, leaving guitar/voice effects effectively dry or in bypass.
/// This processor keeps the plug-in on the realtime audio path while preserving editor and state support.
/// </summary>
internal sealed class DirectVst3EffectProcessor : IDisposable
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

    private DirectVst3EffectProcessor(
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

    public static DirectVst3EffectProcessor Start(
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
            if (!string.IsNullOrWhiteSpace(loadPath) && File.Exists(loadPath))
            {
                plugin.LoadPreset(loadPath);
            }

            try
            {
                view = plugin.CreateView();
            }
            catch
            {
                // The audio processor remains usable even if the plug-in editor is not hostable.
            }

            return new DirectVst3EffectProcessor(module, plugin, view, reference, statePath);
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
                    FillPluginInput(samples.Slice(offset, frames), frames);
                    Plugin.Process(
                        _pluginInput.AsSpan(0, frames * Plugin.InputChannelCount),
                        _pluginOutput.AsSpan(0, frames * Plugin.OutputChannelCount),
                        frames);
                    MixPluginOutput(samples.Slice(offset, frames), frames, dryMix, mix);
                    offset += frames;
                }
            }
            catch (Exception exception)
            {
                Volatile.Write(
                    ref _failure,
                    $"{Reference.Name} quedó en bypass: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }

    public Task<Vst3EffectEditorResult> OpenEditorAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return Task.FromResult(new Vst3EffectEditorResult(
                false,
                Failure ?? $"{Reference.Name} no está activo."));
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
            _disposed = true;
            try
            {
                SaveState();
            }
            catch
            {
            }
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

    private void FillPluginInput(ReadOnlySpan<float> mono, int frames)
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

    private void MixPluginOutput(Span<float> mono, int frames, float dryMix, float wetMix)
    {
        for (var frame = 0; frame < frames; frame++)
        {
            var wet = Plugin.OutputChannelCount == 1
                ? _pluginOutput[frame]
                : (_pluginOutput[frame * 2] + _pluginOutput[(frame * 2) + 1]) * 0.5f;
            if (!float.IsFinite(wet))
            {
                throw new InvalidDataException("El plugin produjo una muestra de audio no válida.");
            }
            mono[frame] = Math.Clamp((mono[frame] * dryMix) + (wet * wetMix), -1f, 1f);
        }
    }

    private void SaveState()
    {
        if (string.IsNullOrWhiteSpace(_statePath) || _disposed && Plugin is null)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            Plugin.SavePreset(_statePath);
        }
        catch
        {
            // State persistence is optional; an unsupported plug-in must still remain usable.
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
