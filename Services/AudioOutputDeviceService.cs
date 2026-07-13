using DrumPracticeStudio.Models;
using DrumPracticeStudio.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DrumPracticeStudio.Services;

public sealed class AudioOutputDeviceService
{
    public IReadOnlyList<AudioOutputDeviceItem> GetDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var defaultId = defaultDevice.ID;
        var devices = new List<AudioOutputDeviceItem>();

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (device)
            {
                devices.Add(new AudioOutputDeviceItem(
                    device.ID,
                    device.FriendlyName,
                    string.Equals(device.ID, defaultId, StringComparison.Ordinal)));
            }
        }

        try
        {
            foreach (var driverName in AsioOut.GetDriverNames())
            {
                devices.Add(new AudioOutputDeviceItem(
                    AudioOutputDeviceId.ForAsio(driverName),
                    driverName,
                    false,
                    AudioOutputBackend.Asio));
            }
        }
        catch
        {
            // Un registro ASIO dañado no debe ocultar las salidas WASAPI disponibles.
        }

        return devices
            .OrderByDescending(device => device.IsDefault)
            .ThenByDescending(device => device.IsAsio)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
