using DrumPracticeStudio.Infrastructure;

namespace DrumPracticeStudio.Models;

public enum AudioInputProfileKind
{
    Clean,
    Voice,
    GuitarClean,
    GuitarDrive,
    Bass,
    Drums
}

public sealed record AudioInputProfileOption(
    AudioInputProfileKind Kind,
    string Label,
    string ProcessingLabel);

public static class AudioInputProfileCatalog
{
    public static IReadOnlyList<AudioInputProfileOption> Options { get; } =
    [
        new(AudioInputProfileKind.Clean, "Limpio", "Sin efectos · solamente ganancia"),
        new(AudioInputProfileKind.Voice, "Voz", "Filtro de graves · EQ · compresor · reverb"),
        new(AudioInputProfileKind.GuitarClean, "Guitarra limpia", "Filtro · compresor · EQ · saturación suave"),
        new(AudioInputProfileKind.GuitarDrive, "Guitarra con distorsión", "Puerta · distorsión · EQ · compresor"),
        new(AudioInputProfileKind.Bass, "Bajo", "Filtro subsónico · compresor · EQ · saturación"),
        new(AudioInputProfileKind.Drums, "Batería", "Filtro · puerta · transitorios · compresor")
    ];

    public static AudioInputProfileOption Get(AudioInputProfileKind kind) =>
        Options.FirstOrDefault(option => option.Kind == kind) ?? Options[0];
}

public sealed record AudioInputMonitorSetting(
    int ChannelIndex,
    float Gain,
    AudioInputProfileKind Profile = AudioInputProfileKind.Clean);

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
    private AudioInputProfileKind _profile;

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

    public AudioInputProfileKind Profile
    {
        get => _profile;
        set
        {
            if (SetProperty(ref _profile, value))
            {
                OnPropertyChanged(nameof(ProcessingLabel));
            }
        }
    }

    public IReadOnlyList<AudioInputProfileOption> ProfileOptions => AudioInputProfileCatalog.Options;
    public string DisplayName => $"Entrada {ChannelIndex + 1} · {Name}";
    public string GainLabel => $"{Gain * 100:0}%";
    public string ProcessingLabel => AudioInputProfileCatalog.Get(Profile).ProcessingLabel;

    public AudioInputMonitorSetting ToSetting() =>
        new(ChannelIndex, (float)Math.Clamp(Gain, 0d, 1.5d), Profile);
}
