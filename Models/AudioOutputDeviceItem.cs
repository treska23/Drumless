namespace DrumPracticeStudio.Models;

public enum AudioOutputBackend
{
    Wasapi,
    Asio
}

public sealed record AudioOutputDeviceItem(
    string Id,
    string Name,
    bool IsDefault,
    AudioOutputBackend Backend = AudioOutputBackend.Wasapi)
{
    public bool IsAsio => Backend == AudioOutputBackend.Asio;

    public string DisplayName => IsAsio
        ? $"{Name} · ASIO directo"
        : IsDefault
            ? $"{Name} · predeterminada · WASAPI"
            : $"{Name} · WASAPI";
}
