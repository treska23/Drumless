namespace DrumPracticeStudio.Models;

public sealed record AudioOutputDeviceItem(string Id, string Name, bool IsDefault)
{
    public string DisplayName => IsDefault ? $"{Name} · predeterminada" : Name;
}
