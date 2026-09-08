using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public static class RecordingOutputEndpointResolver
{
    public static IReadOnlyList<AudioOutputDeviceItem> ResolveCandidates(
        AudioOutputDeviceItem selected,
        IEnumerable<AudioOutputDeviceItem> available)
    {
        ArgumentNullException.ThrowIfNull(selected);
        ArgumentNullException.ThrowIfNull(available);

        var wasapi = available
            .Where(device => !device.IsAsio)
            .ToArray();

        if (!selected.IsAsio)
        {
            var exact = wasapi.FirstOrDefault(device =>
                string.Equals(device.Id, selected.Id, StringComparison.OrdinalIgnoreCase));
            return exact is null ? [] : [exact];
        }

        return wasapi
            .Select(device => new
            {
                Device = device,
                Score = YouTubeOutputDeviceMatcher.ScoreName(selected.Name, device.Name)
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Device.IsDefault)
            .ThenBy(candidate => candidate.Device.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(4)
            .Select(candidate => candidate.Device)
            .ToArray();
    }
}
