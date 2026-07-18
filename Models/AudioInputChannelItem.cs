using System.Collections.ObjectModel;
using System.ComponentModel;
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

public enum AudioEffectKind
{
    HighPass,
    Gate,
    Equalizer,
    Compressor,
    Saturation,
    Reverb,
    Transient,
    ExternalVst3
}

public sealed record AudioEffectKindOption(AudioEffectKind Kind, string Label);

public static class AudioEffectCatalog
{
    public const int MaximumSlots = 4;

    public static IReadOnlyList<AudioEffectKindOption> Options { get; } =
    [
        new(AudioEffectKind.HighPass, "Filtro de graves"),
        new(AudioEffectKind.Gate, "Puerta"),
        new(AudioEffectKind.Equalizer, "Ecualizador"),
        new(AudioEffectKind.Compressor, "Compresor"),
        new(AudioEffectKind.Saturation, "Saturación / distorsión"),
        new(AudioEffectKind.Reverb, "Reverb"),
        new(AudioEffectKind.Transient, "Transitorios"),
        new(AudioEffectKind.ExternalVst3, "Plugin VST3 externo")
    ];

    public static string GetLabel(AudioEffectKind kind) =>
        Options.FirstOrDefault(option => option.Kind == kind)?.Label ?? kind.ToString();
}

public sealed record Vst3EffectReference(
    string ModulePath,
    string ModuleName,
    string ClassId,
    string Category,
    string Name,
    string Vendor,
    string Version,
    string SdkVersion,
    string SubCategories,
    string? PresetPath = null);

public sealed record AudioEffectSlotSetting(
    string Id,
    AudioEffectKind Kind,
    bool IsEnabled = true,
    double Amount = 0.5d,
    double Mix = 1d,
    Vst3EffectReference? ExternalVst3 = null)
{
    public static AudioEffectSlotSetting Create(
        AudioEffectKind kind,
        double amount = 0.5d,
        double mix = 1d,
        Vst3EffectReference? externalVst3 = null) => Normalize(new AudioEffectSlotSetting(
        Guid.NewGuid().ToString("N"),
        kind,
        true,
        amount,
        mix,
        externalVst3));

    public static AudioEffectSlotSetting Normalize(AudioEffectSlotSetting setting) => setting with
    {
        Id = string.IsNullOrWhiteSpace(setting.Id)
            ? Guid.NewGuid().ToString("N")
            : setting.Id,
        Kind = Enum.IsDefined(setting.Kind) ? setting.Kind : AudioEffectKind.Equalizer,
        Amount = double.IsFinite(setting.Amount) ? Math.Clamp(setting.Amount, 0d, 1d) : 0.5d,
        Mix = double.IsFinite(setting.Mix) ? Math.Clamp(setting.Mix, 0d, 1d) : 1d,
        ExternalVst3 = setting.Kind == AudioEffectKind.ExternalVst3
            ? setting.ExternalVst3
            : null
    };
}

public static class AudioInputEffectPresetCatalog
{
    public static IReadOnlyList<AudioEffectSlotSetting> Create(AudioInputProfileKind profile) =>
        profile switch
        {
            AudioInputProfileKind.Voice =>
            [
                AudioEffectSlotSetting.Create(AudioEffectKind.HighPass, 0.38d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Equalizer, 0.62d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Compressor, 0.58d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Reverb, 0.22d, 0.35d)
            ],
            AudioInputProfileKind.GuitarClean =>
            [
                AudioEffectSlotSetting.Create(AudioEffectKind.HighPass, 0.28d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Compressor, 0.38d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Equalizer, 0.55d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Saturation, 0.2d, 0.45d)
            ],
            AudioInputProfileKind.GuitarDrive =>
            [
                AudioEffectSlotSetting.Create(AudioEffectKind.Gate, 0.45d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Saturation, 0.82d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Equalizer, 0.62d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Compressor, 0.38d)
            ],
            AudioInputProfileKind.Bass =>
            [
                AudioEffectSlotSetting.Create(AudioEffectKind.HighPass, 0.08d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Compressor, 0.68d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Equalizer, 0.32d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Saturation, 0.25d, 0.5d)
            ],
            AudioInputProfileKind.Drums =>
            [
                AudioEffectSlotSetting.Create(AudioEffectKind.HighPass, 0.05d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Gate, 0.25d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Transient, 0.55d),
                AudioEffectSlotSetting.Create(AudioEffectKind.Compressor, 0.45d)
            ],
            _ => []
        };
}

public sealed record AudioInputMonitorSetting(
    int ChannelIndex,
    float Gain,
    AudioInputProfileKind Profile = AudioInputProfileKind.Clean,
    IReadOnlyList<AudioEffectSlotSetting>? Effects = null,
    bool EffectsBypassed = false)
{
    public IReadOnlyList<AudioEffectSlotSetting> EffectiveEffects =>
        (Effects ?? AudioInputEffectPresetCatalog.Create(Profile))
        .Take(AudioEffectCatalog.MaximumSlots)
        .Select(AudioEffectSlotSetting.Normalize)
        .ToArray();

    public bool HasExternalEffects =>
        EffectiveEffects.Any(effect =>
            effect.IsEnabled &&
            effect.Kind == AudioEffectKind.ExternalVst3 &&
            effect.ExternalVst3 is not null);

    public bool EffectConfigurationEquals(AudioInputMonitorSetting other) =>
        Profile == other.Profile &&
        EffectsBypassed == other.EffectsBypassed &&
        EffectiveEffects.SequenceEqual(other.EffectiveEffects);
}

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
    private bool _effectsBypassed;

    public AudioInputMonitorItem()
    {
        EffectSlots = [];
    }

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
            if (_profile != value)
            {
                _profile = value;
                ReplaceEffects(AudioInputEffectPresetCatalog.Create(value));
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProcessingLabel));
            }
        }
    }

    public ObservableCollection<AudioEffectSlotItem> EffectSlots { get; }

    public bool EffectsBypassed
    {
        get => _effectsBypassed;
        set
        {
            if (SetProperty(ref _effectsBypassed, value))
            {
                OnPropertyChanged(nameof(ProcessingLabel));
            }
        }
    }

    public IReadOnlyList<AudioInputProfileOption> ProfileOptions => AudioInputProfileCatalog.Options;
    public string DisplayName => $"Entrada {ChannelIndex + 1} · {Name}";
    public string GainLabel => $"{Gain * 100:0}%";
    public string ProcessingLabel => EffectsBypassed
        ? "Cadena omitida · señal limpia"
        : EffectSlots.Count == 0
            ? "Sin efectos · solamente ganancia"
            : string.Join(
                " · ",
                EffectSlots.Select(slot => slot.IsEnabled
                    ? slot.DisplayName
                    : $"{slot.DisplayName} (bypass)"));

    public AudioInputMonitorSetting ToSetting() =>
        new(
            ChannelIndex,
            (float)Math.Clamp(Gain, 0d, 1.5d),
            Profile,
            EffectSlots.Select(slot => slot.ToSetting()).ToArray(),
            EffectsBypassed);

    public void LoadEffects(
        IEnumerable<AudioEffectSlotSetting>? effects,
        bool bypassed)
    {
        ReplaceEffects(effects ?? AudioInputEffectPresetCatalog.Create(Profile));
        EffectsBypassed = bypassed;
    }

    public void ReplaceEffects(IEnumerable<AudioEffectSlotSetting> effects)
    {
        foreach (var item in EffectSlots)
        {
            item.PropertyChanged -= OnEffectSlotPropertyChanged;
        }
        EffectSlots.Clear();
        foreach (var effect in effects.Take(AudioEffectCatalog.MaximumSlots))
        {
            var item = new AudioEffectSlotItem(effect);
            item.PropertyChanged += OnEffectSlotPropertyChanged;
            EffectSlots.Add(item);
        }
        OnPropertyChanged(nameof(EffectSlots));
        OnPropertyChanged(nameof(ProcessingLabel));
    }

    public bool AddEffect(AudioEffectKind kind = AudioEffectKind.Equalizer)
    {
        if (EffectSlots.Count >= AudioEffectCatalog.MaximumSlots)
        {
            return false;
        }
        var item = new AudioEffectSlotItem(AudioEffectSlotSetting.Create(kind));
        item.PropertyChanged += OnEffectSlotPropertyChanged;
        EffectSlots.Add(item);
        OnPropertyChanged(nameof(EffectSlots));
        OnPropertyChanged(nameof(ProcessingLabel));
        return true;
    }

    public bool RemoveEffect(AudioEffectSlotItem item)
    {
        if (!EffectSlots.Remove(item))
        {
            return false;
        }
        item.PropertyChanged -= OnEffectSlotPropertyChanged;
        OnPropertyChanged(nameof(EffectSlots));
        OnPropertyChanged(nameof(ProcessingLabel));
        return true;
    }

    public bool MoveEffect(AudioEffectSlotItem item, int direction)
    {
        var index = EffectSlots.IndexOf(item);
        var target = Math.Clamp(index + Math.Sign(direction), 0, EffectSlots.Count - 1);
        if (index < 0 || target == index)
        {
            return false;
        }
        EffectSlots.Move(index, target);
        OnPropertyChanged(nameof(EffectSlots));
        OnPropertyChanged(nameof(ProcessingLabel));
        return true;
    }

    private void OnEffectSlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(EffectSlots));
        OnPropertyChanged(nameof(ProcessingLabel));
    }
}

public sealed class AudioEffectSlotItem : ObservableObject
{
    private AudioEffectKind _kind;
    private bool _isEnabled;
    private double _amount;
    private double _mix;
    private Vst3EffectReference? _externalVst3;

    public AudioEffectSlotItem(AudioEffectSlotSetting setting)
    {
        var normalized = AudioEffectSlotSetting.Normalize(setting);
        Id = normalized.Id;
        _kind = normalized.Kind;
        _isEnabled = normalized.IsEnabled;
        _amount = normalized.Amount;
        _mix = normalized.Mix;
        _externalVst3 = normalized.ExternalVst3;
    }

    public string Id { get; }
    public IReadOnlyList<AudioEffectKindOption> KindOptions => AudioEffectCatalog.Options;

    public AudioEffectKind Kind
    {
        get => _kind;
        set
        {
            if (SetProperty(ref _kind, value))
            {
                if (value != AudioEffectKind.ExternalVst3)
                {
                    ExternalVst3 = null;
                }
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(IsExternal));
            }
        }
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public double Amount
    {
        get => _amount;
        set => SetProperty(ref _amount, Math.Clamp(value, 0d, 1d));
    }

    public double Mix
    {
        get => _mix;
        set => SetProperty(ref _mix, Math.Clamp(value, 0d, 1d));
    }

    public Vst3EffectReference? ExternalVst3
    {
        get => _externalVst3;
        set
        {
            if (SetProperty(ref _externalVst3, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public bool IsExternal => Kind == AudioEffectKind.ExternalVst3;
    public string DisplayName => IsExternal
        ? ExternalVst3?.Name ?? "VST3 sin seleccionar"
        : AudioEffectCatalog.GetLabel(Kind);

    public AudioEffectSlotSetting ToSetting() => AudioEffectSlotSetting.Normalize(new(
        Id,
        Kind,
        IsEnabled,
        Amount,
        Mix,
        ExternalVst3));
}
