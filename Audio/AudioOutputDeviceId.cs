namespace DrumPracticeStudio.Audio;

internal static class AudioOutputDeviceId
{
    private const string AsioPrefix = "asio:";

    public static string ForAsio(string driverName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(driverName);
        return AsioPrefix + driverName;
    }

    public static bool TryGetAsioDriverName(string? deviceId, out string driverName)
    {
        if (deviceId?.StartsWith(AsioPrefix, StringComparison.Ordinal) == true &&
            deviceId.Length > AsioPrefix.Length)
        {
            driverName = deviceId[AsioPrefix.Length..];
            return true;
        }

        driverName = string.Empty;
        return false;
    }
}
