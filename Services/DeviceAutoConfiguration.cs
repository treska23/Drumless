using DrumPracticeStudio.Midi;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

internal static class DeviceAutoConfiguration
{
    private static readonly HashSet<string> GenericAudioTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio", "asio", "usb", "device", "output", "speakers", "speaker",
        "altavoces", "salida", "predeterminada", "directo", "windows"
    };

    private static readonly string[] GenericAsioDrivers =
    [
        "asio4all", "flexasio", "generic low latency", "magix low latency"
    ];

    private static readonly string[] PreferredMidiTerms =
    [
        "drum", "bateria", "batería", "alesis", "roland", "efnote", "atv",
        "dtx", "td-", "mpk", "launchkey", "keylab"
    ];

    public static AudioOutputDeviceItem? SelectAudioOutput(
        IReadOnlyList<AudioOutputDeviceItem> devices,
        string? savedDeviceId)
    {
        var saved = devices.FirstOrDefault(device =>
            string.Equals(device.Id, savedDeviceId, StringComparison.Ordinal));
        if (saved is not null)
        {
            return saved;
        }

        var defaultWasapi = devices.FirstOrDefault(device => device.IsDefault) ??
                            devices.FirstOrDefault(device => !device.IsAsio);
        var asioDevices = devices.Where(device => device.IsAsio).ToArray();
        if (defaultWasapi is not null)
        {
            var defaultTokens = SignificantTokens(defaultWasapi.Name);
            var matchingAsio = asioDevices
                .Select(device => new
                {
                    Device = device,
                    Score = SignificantTokens(device.Name).Count(defaultTokens.Contains)
                })
                .OrderByDescending(candidate => candidate.Score)
                .FirstOrDefault();
            if (matchingAsio?.Score > 0)
            {
                return matchingAsio.Device;
            }
        }

        var nativeAsio = asioDevices
            .Where(device => !GenericAsioDrivers.Any(term =>
                device.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return nativeAsio.Length == 1
            ? nativeAsio[0]
            : defaultWasapi ?? devices.FirstOrDefault();
    }

    public static MidiDeviceItem? SelectMidiInput(
        IReadOnlyList<MidiDeviceItem> devices,
        string? savedName,
        int? savedIndex)
    {
        var exact = devices.FirstOrDefault(device =>
            device.Index == savedIndex &&
            string.Equals(device.Name, savedName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        var sameName = devices.FirstOrDefault(device =>
            string.Equals(device.Name, savedName, StringComparison.OrdinalIgnoreCase));
        if (sameName is not null)
        {
            return sameName;
        }

        return devices.FirstOrDefault(device => PreferredMidiTerms.Any(term =>
                   device.Name.Contains(term, StringComparison.OrdinalIgnoreCase))) ??
               devices.FirstOrDefault();
    }

    private static HashSet<string> SignificantTokens(string name)
    {
        var tokens = name
            .ToLowerInvariant()
            .Split(
                [' ', '-', '_', '(', ')', '[', ']', '.', ',', ':'],
                StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 4 && !GenericAudioTokens.Contains(token));
        return new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
    }
}
