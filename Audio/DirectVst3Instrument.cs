using DrumPracticeStudio.Models;
using DrumPracticeStudio.Views;
using NAudio.Vst3;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class DirectVst3Instrument : IDisposable
{
    private readonly Vst3Module _module;
    private readonly Vst3PluginView? _view;
    private Vst3EditorWindow? _editor;
    private bool _disposed;

    private DirectVst3Instrument(
        Vst3Module module,
        Vst3Plugin plugin,
        Vst3PluginView? view,
        ISampleProvider provider,
        string displayName)
    {
        _module = module;
        Plugin = plugin;
        _view = view;
        Provider = provider;
        DisplayName = displayName;
    }

    public Vst3Plugin Plugin { get; }
    public ISampleProvider Provider { get; }
    public string DisplayName { get; }
    public IReadOnlyList<string> Programs =>
        Plugin.ActiveProgramList?.Programs ?? Array.Empty<string>();
    public int CurrentProgram => Plugin.CurrentProgram;
    public uint LatencySamples => Plugin.LatencySamples;

    public static DirectVst3Instrument Load(Vst3InstrumentItem instrument, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Vst3Module? module = null;
        Vst3Plugin? plugin = null;
        Vst3PluginView? view = null;
        try
        {
            module = Vst3Module.Load(instrument.Module.Path);
            plugin = module.CreatePlugin(
                instrument.PluginClass,
                sampleRate,
                AudioLatencySettings.VstMaxBlockSize);
            if (!plugin.IsInstrument)
            {
                throw new InvalidOperationException(
                    $"{instrument.DisplayName} no se identificó como instrumento VST3.");
            }

            var provider = new Vst3InstrumentSampleProvider(plugin);
            if (provider.WaveFormat.Channels != 2)
            {
                throw new NotSupportedException(
                    $"{instrument.DisplayName} ha abierto {provider.WaveFormat.Channels} canales; " +
                    "el motor ASIO directo necesita una salida principal estéreo.");
            }

            view = plugin.CreateView();
            return new DirectVst3Instrument(
                module,
                plugin,
                view,
                provider,
                instrument.DisplayName);
        }
        catch
        {
            view?.Dispose();
            plugin?.Dispose();
            module?.Dispose();
            throw;
        }
    }

    public void SendNoteOn(int note, int velocity, int channel) =>
        Plugin.SendNoteOn(
            Math.Clamp(note, 0, 127),
            Math.Clamp(velocity, 1, 127) / 127f,
            Math.Clamp(channel - 1, 0, 15));

    public void SendNoteOff(int note, int velocity, int channel) =>
        Plugin.SendNoteOff(
            Math.Clamp(note, 0, 127),
            Math.Clamp(velocity, 0, 127) / 127f,
            Math.Clamp(channel - 1, 0, 15));

    public void SendControlChange(int controller, int value) =>
        Plugin.SendControlChange(
            Math.Clamp(controller, 0, 127),
            Math.Clamp(value, 0, 127) / 127d);

    public void Panic() => Plugin.AllNotesOff();

    public void SelectProgram(int programIndex)
    {
        if (!Plugin.SupportsProgramChange ||
            programIndex < 0 ||
            programIndex >= Programs.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(programIndex),
                "El instrumento no expone ese programa mediante VST3.");
        }

        Plugin.SendProgramChange(programIndex);
    }

    public void LoadPreset(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Plugin.LoadPreset(path);
    }

    public void SavePreset(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
        Plugin.SavePreset(path);
    }

    public bool OpenEditor()
    {
        if (_view is null)
        {
            return false;
        }

        if (_editor is not null)
        {
            _editor.Activate();
            return true;
        }

        _editor = new Vst3EditorWindow(DisplayName, _view)
        {
            ShowInTaskbar = true
        };
        _editor.ClosedByUser += (_, _) => _editor = null;
        _editor.Show();
        _editor.Activate();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var editor = _editor;
        _editor = null;
        try
        {
            editor?.Close();
        }
        catch
        {
            // El editor puede haberse cerrado por iniciativa del plugin.
        }

        try
        {
            Plugin.AllNotesOff();
        }
        catch
        {
            // El plugin puede estar terminando después de un fallo nativo.
        }

        _view?.Dispose();
        Plugin.Dispose();
        _module.Dispose();
    }
}
