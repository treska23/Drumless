using DrumPracticeStudio.Infrastructure;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.ViewModels;

public sealed class PadViewModel(DrumPad pad) : ObservableObject
{
    private bool _isHit;

    public DrumPad Pad { get; } = pad;
    public string Id => Pad.Id;
    public string Name => Pad.Name;
    public string ShortName => Pad.ShortName;
    public string Articulation => Pad.Articulation;
    public string Accent => Pad.Accent;
    public string MidiLabel => Pad.MidiLabel;
    public string SampleLabel => Pad.SampleLabel;

    public bool IsHit
    {
        get => _isHit;
        set => SetProperty(ref _isHit, value);
    }

    public async Task FlashAsync()
    {
        IsHit = true;
        await Task.Delay(90);
        IsHit = false;
    }
}
