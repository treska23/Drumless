using NAudio.Midi;

namespace DrumPracticeStudio.Midi;

public sealed record MidiDeviceItem(int Index, string Name)
{
    public string DisplayName => $"{Name} · entrada {Index + 1}";
}

public sealed record MidiNoteMessage(int Note, int Velocity, int Channel);

public sealed class MidiInputService : IDisposable
{
    private MidiIn? _input;

    public event EventHandler<MidiNoteMessage>? NoteReceived;
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
        if (_input is null)
        {
            return;
        }

        _input.MessageReceived -= OnMessageReceived;
        _input.ErrorReceived -= OnErrorReceived;
        try
        {
            _input.Stop();
        }
        catch
        {
            // El dispositivo puede haberse desconectado físicamente.
        }

        _input.Dispose();
        _input = null;
    }

    public void Dispose() => Disconnect();

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs args)
    {
        if (args.MidiEvent is NoteOnEvent noteOn && noteOn.Velocity > 0)
        {
            NoteReceived?.Invoke(this, new MidiNoteMessage(
                noteOn.NoteNumber,
                noteOn.Velocity,
                noteOn.Channel));
        }
    }

    private void OnErrorReceived(object? sender, MidiInMessageEventArgs args) =>
        Error?.Invoke(this, args.MidiEvent?.ToString() ?? "Error MIDI desconocido");
}
