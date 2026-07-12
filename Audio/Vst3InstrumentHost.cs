using DrumPracticeStudio.Models;
using NAudio.Vst3;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class Vst3InstrumentHost : IDisposable
{
    private const int MaximumBlockSize = 2_048;

    private Vst3Module? _module;
    private Vst3Plugin? _plugin;
    private Vst3PluginView? _view;

    public ISampleProvider? Provider { get; private set; }
    public Vst3PluginView? View => _view;
    public bool IsLoaded => _plugin is not null;
    public string? DisplayName { get; private set; }

    public ISampleProvider Load(Vst3InstrumentItem instrument, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        Unload();

        Vst3Module? module = null;
        Vst3Plugin? plugin = null;
        Vst3PluginView? view = null;
        try
        {
            module = Vst3Module.Load(instrument.Module.Path);
            plugin = module.CreatePlugin(instrument.PluginClass, sampleRate, MaximumBlockSize);
            if (!plugin.IsInstrument)
            {
                throw new InvalidOperationException($"{instrument.DisplayName} no se identificó como instrumento VST3.");
            }

            var provider = new Vst3InstrumentSampleProvider(plugin);
            if (provider.WaveFormat.Channels != 2)
            {
                throw new NotSupportedException(
                    $"{instrument.DisplayName} ha abierto {provider.WaveFormat.Channels} canales. " +
                    "Esta primera versión necesita una salida principal estéreo.");
            }

            view = plugin.CreateView();
            _module = module;
            _plugin = plugin;
            _view = view;
            Provider = provider;
            DisplayName = instrument.DisplayName;
            return provider;
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
        _plugin?.SendNoteOn(
            Math.Clamp(note, 0, 127),
            Math.Clamp(velocity, 1, 127) / 127f,
            Math.Clamp(channel - 1, 0, 15));

    public void SendNoteOff(int note, int velocity, int channel) =>
        _plugin?.SendNoteOff(
            Math.Clamp(note, 0, 127),
            Math.Clamp(velocity, 0, 127) / 127f,
            Math.Clamp(channel - 1, 0, 15));

    public void SendControlChange(int controller, int value) =>
        _plugin?.SendControlChange(
            Math.Clamp(controller, 0, 127),
            Math.Clamp(value, 0, 127) / 127d);

    public void Panic() => _plugin?.AllNotesOff();

    public void Unload()
    {
        try
        {
            _plugin?.AllNotesOff();
        }
        catch
        {
            // El instrumento puede estar cerrándose después de un fallo interno.
        }

        _view?.Dispose();
        _view = null;
        _plugin?.Dispose();
        _plugin = null;
        _module?.Dispose();
        _module = null;
        Provider = null;
        DisplayName = null;
    }

    public void Dispose() => Unload();
}
