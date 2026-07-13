using DrumPracticeStudio.Models;
using NAudio.CoreAudioApi;

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

        return devices
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }
}
