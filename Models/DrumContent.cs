using System.Collections.ObjectModel;
using DrumPracticeStudio.Infrastructure;

namespace DrumPracticeStudio.Models;

public sealed class SoundLibrary
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Category { get; init; }
    public required string Accent { get; init; }
    public bool IsFactory { get; init; }
    public ObservableCollection<DrumKit> Kits { get; } = [];
    public string KitCountLabel => Kits.Count == 1 ? "1 kit" : $"{Kits.Count} kits";
}

public sealed class DrumKit
{
    public required string Id { get; init; }
    public required string LibraryId { get; init; }
    public required string Name { get; set; }
    public required string Description { get; set; }
    public required string Category { get; set; }
    public required string Accent { get; set; }
    public bool IsFactory { get; set; }
    public ObservableCollection<DrumPad> Pads { get; } = [];
    public string PadCountLabel => $"{Pads.Count} instrumentos";
}

public sealed class DrumPad
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ShortName { get; init; }
    public required string Articulation { get; init; }
    public required string Accent { get; init; }
    public required int DefaultMidiNote { get; init; }
    public string? ChokeGroup { get; init; }
    public bool ChokeExisting { get; init; }
    public ObservableCollection<SampleLayer> Layers { get; } = [];

    public string MidiLabel => $"MIDI {DefaultMidiNote}";
    public string SampleLabel => Layers.SelectMany(layer => layer.Samples).Count() switch
    {
        0 => "Sin sample",
        1 => "1 sample",
        var count => $"{count} samples"
    };
}

public sealed class SampleLayer
{
    public required int MinVelocity { get; init; }
    public required int MaxVelocity { get; init; }
    public float Gain { get; init; } = 1f;
    public ObservableCollection<SampleReference> Samples { get; } = [];
}

public sealed record SampleReference(string Path, float Gain = 1f);

public sealed class MidiProfile
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public Dictionary<int, string> NoteMappings { get; } = [];

    public bool TryResolve(int note, out string articulation) =>
        NoteMappings.TryGetValue(note, out articulation!);
}

public sealed class LocalTrack : ObservableObject
{
    private bool _isMissing;

    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Path { get; init; }
    public required TrackVariant Variant { get; init; }

    public bool IsMissing
    {
        get => _isMissing;
        set
        {
            if (SetProperty(ref _isMissing, value))
            {
                OnPropertyChanged(nameof(IsAvailable));
                OnPropertyChanged(nameof(AvailabilityLabel));
            }
        }
    }

    public bool IsAvailable => !IsMissing;

    public string AvailabilityLabel => IsMissing ? "Archivo no encontrado" : VariantLabel;

    public string VariantLabel => Variant switch
    {
        TrackVariant.Original => "Original",
        TrackVariant.UserDrumless => "Ya estaba sin batería",
        TrackVariant.GeneratedDrumless => "Sin batería · generada",
        _ => "Pista local"
    };
}

public enum TrackVariant
{
    Original,
    UserDrumless,
    GeneratedDrumless
}
