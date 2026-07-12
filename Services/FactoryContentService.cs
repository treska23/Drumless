using System.Collections.ObjectModel;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed class FactoryContentService
{
    private static readonly (string Id, string Name, string ShortName, int Note, string Accent, string? Choke, bool ChokeExisting)[] PadSpecs =
    [
        ("kick.main", "Bombo", "KICK", 36, "#FFB548", null, false),
        ("snare.center", "Caja", "SNARE", 38, "#FF6B72", null, false),
        ("hihat.closed", "Charles cerrado", "HH CLOSED", 42, "#62D3A4", "hihat", true),
        ("hihat.open", "Charles abierto", "HH OPEN", 46, "#51B9D7", "hihat", true),
        ("tom.low", "Tom grave", "LOW TOM", 45, "#A58BFF", null, false),
        ("tom.high", "Tom agudo", "HIGH TOM", 48, "#C783FF", null, false),
        ("crash.edge", "Crash", "CRASH", 49, "#F7D66A", "crash", false),
        ("ride.bow", "Ride", "RIDE", 51, "#F2A65A", "ride", false)
    ];

    public ObservableCollection<SoundLibrary> Load()
    {
        AppPaths.EnsureCreated();

        var acoustic = CreateLibrary(
            id: "factory.sessions",
            name: "Factory Sessions",
            description: "Baterías acústicas directas y naturales para practicar.",
            category: "Acústica",
            accent: "#FFB548",
            kitName: "Natural Studio",
            kitDescription: "Kit acústico compacto con dos capas de velocidad y round-robin.",
            electronic: false);

        var electronic = CreateLibrary(
            id: "factory.electronic",
            name: "Electronic Lab",
            description: "Sonidos sintéticos con ataque definido para pop y electrónica.",
            category: "Electrónica",
            accent: "#62D3A4",
            kitName: "Neon Pulse",
            kitDescription: "Kit electrónico ágil con colas limpias y pegada moderna.",
            electronic: true);

        var user = new SoundLibrary
        {
            Id = "user.sounds",
            Name = "Mis sonidos",
            Description = "Kits creados al sustituir pads por tus propios WAV.",
            Category = "Usuario",
            Accent = "#A58BFF",
            IsFactory = false
        };

        return [acoustic, electronic, user];
    }

    public DrumKit CloneAsUserKit(DrumKit source, string name)
    {
        var clone = new DrumKit
        {
            Id = $"user.{Guid.NewGuid():N}",
            LibraryId = "user.sounds",
            Name = name,
            Description = $"Kit personalizado basado en {source.Name}.",
            Category = "Personalizado",
            Accent = "#A58BFF",
            IsFactory = false
        };

        foreach (var pad in source.Pads)
        {
            var padClone = new DrumPad
            {
                Id = pad.Id,
                Name = pad.Name,
                ShortName = pad.ShortName,
                Articulation = pad.Articulation,
                Accent = pad.Accent,
                DefaultMidiNote = pad.DefaultMidiNote,
                ChokeGroup = pad.ChokeGroup,
                ChokeExisting = pad.ChokeExisting
            };

            foreach (var layer in pad.Layers)
            {
                var layerClone = new SampleLayer
                {
                    MinVelocity = layer.MinVelocity,
                    MaxVelocity = layer.MaxVelocity,
                    Gain = layer.Gain
                };
                foreach (var sample in layer.Samples)
                {
                    layerClone.Samples.Add(sample with { });
                }

                padClone.Layers.Add(layerClone);
            }

            clone.Pads.Add(padClone);
        }

        return clone;
    }

    private static SoundLibrary CreateLibrary(
        string id,
        string name,
        string description,
        string category,
        string accent,
        string kitName,
        string kitDescription,
        bool electronic)
    {
        var library = new SoundLibrary
        {
            Id = id,
            Name = name,
            Description = description,
            Category = category,
            Accent = accent,
            IsFactory = true
        };

        var kit = new DrumKit
        {
            Id = electronic ? "factory.neon-pulse" : "factory.natural-studio",
            LibraryId = id,
            Name = kitName,
            Description = kitDescription,
            Category = category,
            Accent = accent,
            IsFactory = true
        };

        foreach (var spec in PadSpecs)
        {
            kit.Pads.Add(CreatePad(spec, electronic));
        }

        library.Kits.Add(kit);
        return library;
    }

    private static DrumPad CreatePad(
        (string Id, string Name, string ShortName, int Note, string Accent, string? Choke, bool ChokeExisting) spec,
        bool electronic)
    {
        var family = electronic ? "electronic" : "acoustic";
        var pad = new DrumPad
        {
            Id = spec.Id,
            Name = spec.Name,
            ShortName = spec.ShortName,
            Articulation = spec.Id,
            Accent = spec.Accent,
            DefaultMidiNote = spec.Note,
            ChokeGroup = spec.Choke,
            ChokeExisting = spec.ChokeExisting
        };

        pad.Layers.Add(CreateLayer(family, spec.Id, electronic, hard: false, 1, 79));
        pad.Layers.Add(CreateLayer(family, spec.Id, electronic, hard: true, 80, 127));
        return pad;
    }

    private static SampleLayer CreateLayer(
        string family,
        string voice,
        bool electronic,
        bool hard,
        int minVelocity,
        int maxVelocity)
    {
        var layer = new SampleLayer
        {
            MinVelocity = minVelocity,
            MaxVelocity = maxVelocity,
            Gain = 0.9f
        };

        for (var variation = 1; variation <= 2; variation++)
        {
            var safeVoice = voice.Replace('.', '-');
            var fileName = $"{safeVoice}-{(hard ? "hard" : "soft")}-rr{variation}.wav";
            var path = Path.Combine(AppPaths.FactoryContent, family, fileName);
            FactorySoundGenerator.EnsureSample(path, voice, electronic, hard, variation);
            layer.Samples.Add(new SampleReference(path));
        }

        return layer;
    }
}
