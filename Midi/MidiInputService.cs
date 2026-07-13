using NAudio.Midi;

namespace DrumPracticeStudio.Midi;

public sealed record MidiDeviceItem(int Index, string Name)
{
    public string DisplayName => $"{Name} · entrada {Index + 1}";
}

public sealed record MidiNoteMessage(int Note, int Velocity, int Channel);
public sealed record MidiNoteOffMessage(int Note, int Velocity, int Channel);
public sealed record MidiControlChangeMessage(int Controller, int Value, int Channel);

public sealed class MidiInputService : IDisposable
{
    private MidiIn? _input;

    public event EventHandler<MidiNoteMessage>? NoteReceived;
    public event EventHandler<MidiNoteOffMessage>? NoteOffReceived;
    public event EventHandler<MidiControlChangeMessage>? ControlChangeReceived;
    public event EventHandler<string>? Error;

    public bool IsConnected => _input is not null;

    public IReadOnlyList<MidiDeviceItem> GetDevices()
    {
        var devices = new List<MidiDeviceItem>();
        try
        {
            for (var index = 0; index < MidiIn.NumberOfDevices; index++)
            {
                var info = MidiIn.DeviceInfo(index);
                devices.Add(new MidiDeviceItem(index, info.ProductName));
            }
        }
        catch (Exception exception)
        {
            Error?.Invoke(this, exception.Message);
        }

        return devices;
    }

    public void Connect(int deviceIndex)
    {
        Disconnect();
        try
        {
            _input = new MidiIn(deviceIndex);
            _input.MessageReceived += OnMessageReceived;
            _input.ErrorReceived += OnErrorReceived;
            _input.Start();
        }
        catch
        {
            Disconnect();
            throw;
        }
    }

    public void Disconnect()
    {
        var input = Interlocked.Exchange(ref _input, null);
        if (input is null)
        {
            return;
        }

        input.MessageReceived -= OnMessageReceived;
        input.ErrorReceived -= OnErrorReceived;
        try
        {
            input.Stop();
        }
        catch
        {
            // El dispositivo puede haberse desconectado físicamente.
        }

        try
        {
            input.Dispose();
        }
        catch
        {
            // MidiIn.Dispose llama a midiInReset. Windows devuelve NoDriver si el
            // controlador desapareció mientras se cambiaba a otro dispositivo.
            // La instancia ya está desacoplada y ese error de limpieza no debe
            // cerrar la aplicación ni impedir abrir la nueva entrada MIDI.
        }
    }

    public void Dispose() => Disconnect();

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs args)
    {
        switch (args.MidiEvent)
        {
            case NoteOnEvent noteOn when noteOn.Velocity > 0:
                NoteReceived?.Invoke(this, new MidiNoteMessage(
                    noteOn.NoteNumber,
                    noteOn.Velocity,
                    noteOn.Channel));
                break;
            case NoteEvent note when note.CommandCode is MidiCommandCode.NoteOff or MidiCommandCode.NoteOn:
                NoteOffReceived?.Invoke(this, new MidiNoteOffMessage(
                    note.NoteNumber,
                    note.Velocity,
                    note.Channel));
                break;
            case ControlChangeEvent controlChange:
                ControlChangeReceived?.Invoke(this, new MidiControlChangeMessage(
                    (int)controlChange.Controller,
                    controlChange.ControllerValue,
                    controlChange.Channel));
                break;
        }
    }

    private void OnErrorReceived(object? sender, MidiInMessageEventArgs args) =>
        Error?.Invoke(this, args.MidiEvent?.ToString() ?? "Error MIDI desconocido");
}
