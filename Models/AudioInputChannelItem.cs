namespace DrumPracticeStudio.Models;

public sealed record AudioInputChannelItem(int? ChannelIndex, string Name)
{
    public bool IsDisabled => ChannelIndex is null;
    public string DisplayName => IsDisabled
        ? "Desactivada"
        : $"Entrada {ChannelIndex!.Value + 1} · {Name}";
}
