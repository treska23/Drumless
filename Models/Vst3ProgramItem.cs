namespace DrumPracticeStudio.Models;

public sealed record Vst3ProgramItem(int Index, string Name)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"Programa {Index + 1}"
        : Name;
}
