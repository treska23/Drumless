using DrumPracticeStudio.Infrastructure;

namespace DrumPracticeStudio.Models;

public sealed record AudioInputMonitorSetting(int ChannelIndex, float Gain);

public sealed record AudioInputChannelItem(int? ChannelIndex, string Name)
{
    public bool IsDisabled => ChannelIndex is null;
    public string DisplayName => IsDisabled
        ? "Desactivada"
        : $"Entrada {ChannelIndex!.Value + 1} · {Name}";
}

public sealed class AudioInputMonitorItem : ObservableObject
{
    private bool _isEnabled;
    private double _gain = 0.8d;

    public required int ChannelIndex { get; init; }
    public required string Name { get; init; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public double Gain
    {
        get => _gain;
        set
        {
            if (SetProperty(ref _gain, Math.Clamp(value, 0d, 1.5d)))
            {
                OnPropertyChanged(nameof(GainLabel));
            }
        }
    }

    public string DisplayName => $"Entrada {ChannelIndex + 1} · {Name}";
    public string GainLabel => $"{Gain * 100:0}%";

    public AudioInputMonitorSetting ToSetting() =>
        new(ChannelIndex, (float)Math.Clamp(Gain, 0d, 1.5d));
}
