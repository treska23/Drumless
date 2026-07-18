using System.Collections.ObjectModel;
using System.ComponentModel;
using DrumPracticeStudio.Infrastructure;

namespace DrumPracticeStudio.Models;

public enum AudioEffectBusTarget
{
    Track,
    YouTube,
    Master
}

public sealed record AudioEffectBusSetting(
    AudioEffectBusTarget Target,
    IReadOnlyList<AudioEffectSlotSetting>? Effects = null,
    bool EffectsBypassed = false)
{
    public IReadOnlyList<AudioEffectSlotSetting> EffectiveEffects =>
        (Effects ?? [])
        .Take(AudioEffectCatalog.MaximumSlots)
        .Select(AudioEffectSlotSetting.Normalize)
        .ToArray();
}

public sealed class AudioEffectBusItem : ObservableObject
{
    private bool _effectsBypassed;

    public AudioEffectBusItem(AudioEffectBusTarget target)
    {
        Target = target;
        EffectSlots = [];
    }

    public AudioEffectBusTarget Target { get; }
    public string Name => Target switch
    {
        AudioEffectBusTarget.Track => "Pista local",
        AudioEffectBusTarget.YouTube => "YouTube",
        AudioEffectBusTarget.Master => "Bus maestro",
        _ => Target.ToString()
    };
    public string Description => Target switch
    {
        AudioEffectBusTarget.Track => "Sólo la pista local antes de mezclar batería e inputs.",
        AudioEffectBusTarget.YouTube => "Sólo el audio capturado del vídeo.",
        AudioEffectBusTarget.Master => "Mezcla de reproducción final antes de la salida.",
        _ => string.Empty
    };
    public ObservableCollection<AudioEffectSlotItem> EffectSlots { get; }

    public bool EffectsBypassed
    {
        get => _effectsBypassed;
        set => SetProperty(ref _effectsBypassed, value);
    }

    public string Summary => EffectsBypassed
        ? "Bypass · señal limpia"
        : EffectSlots.Count == 0
            ? "Sin efectos"
            : string.Join(" · ", EffectSlots.Select(slot => slot.DisplayName));

    public void Load(AudioEffectBusSetting setting)
    {
        foreach (var slot in EffectSlots)
        {
            slot.PropertyChanged -= OnSlotPropertyChanged;
        }
        EffectSlots.Clear();
        foreach (var effect in setting.EffectiveEffects)
        {
            var slot = new AudioEffectSlotItem(effect);
            slot.PropertyChanged += OnSlotPropertyChanged;
            EffectSlots.Add(slot);
        }
        EffectsBypassed = setting.EffectsBypassed;
        OnPropertyChanged(nameof(EffectSlots));
        OnPropertyChanged(nameof(Summary));
    }

    public bool AddEffect()
    {
        if (EffectSlots.Count >= AudioEffectCatalog.MaximumSlots)
        {
            return false;
        }
        var slot = new AudioEffectSlotItem(
            AudioEffectSlotSetting.Create(AudioEffectKind.Equalizer));
        slot.PropertyChanged += OnSlotPropertyChanged;
        EffectSlots.Add(slot);
        OnPropertyChanged(nameof(EffectSlots));
        OnPropertyChanged(nameof(Summary));
        return true;
    }

    public bool RemoveEffect(AudioEffectSlotItem slot)
    {
        if (!EffectSlots.Remove(slot))
        {
            return false;
        }
        slot.PropertyChanged -= OnSlotPropertyChanged;
        OnPropertyChanged(nameof(EffectSlots));
        OnPropertyChanged(nameof(Summary));
        return true;
    }

    public bool MoveEffect(AudioEffectSlotItem slot, int direction)
    {
        var index = EffectSlots.IndexOf(slot);
        var target = Math.Clamp(index + Math.Sign(direction), 0, EffectSlots.Count - 1);
        if (index < 0 || target == index)
        {
            return false;
        }
        EffectSlots.Move(index, target);
        OnPropertyChanged(nameof(EffectSlots));
        OnPropertyChanged(nameof(Summary));
        return true;
    }

    public AudioEffectBusSetting ToSetting() => new(
        Target,
        EffectSlots.Select(slot => slot.ToSetting()).ToArray(),
        EffectsBypassed);

    private void OnSlotPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(EffectSlots));
        OnPropertyChanged(nameof(Summary));
    }
}
