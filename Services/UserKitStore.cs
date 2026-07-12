using System.Text.Json;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed class UserKitStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _manifestPath = Path.Combine(AppPaths.UserLibraries, "user-kits.json");
    private readonly string _samplesPath = Path.Combine(AppPaths.UserLibraries, "Samples");

    public UserKitStore()
    {
        AppPaths.EnsureCreated();
        Directory.CreateDirectory(_samplesPath);
    }

    public IReadOnlyList<DrumKit> Load()
    {
        if (!File.Exists(_manifestPath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_manifestPath);
            var document = JsonSerializer.Deserialize<UserKitDocument>(json, JsonOptions);
            return document?.Kits.Select(ToModel).ToArray() ?? [];
        }
        catch
        {
            // Un manifiesto dañado no debe impedir que la aplicación arranque.
            return [];
        }
    }

    public string ImportSample(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("La primera versión solo admite WAV para kits.");
        }

        var safeName = string.Concat(Path.GetFileNameWithoutExtension(sourcePath)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
        var destination = Path.Combine(_samplesPath, $"{safeName}-{Guid.NewGuid():N}.wav");
        File.Copy(sourcePath, destination, overwrite: false);
        return destination;
    }

    public void Save(IEnumerable<DrumKit> kits)
    {
        var document = new UserKitDocument
        {
            SchemaVersion = 1,
            Kits = kits.Select(ToDto).ToList()
        };

        var temporaryPath = _manifestPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document, JsonOptions));
        File.Move(temporaryPath, _manifestPath, overwrite: true);
    }

    private static DrumKit ToModel(KitDto dto)
    {
        var kit = new DrumKit
        {
            Id = dto.Id,
            LibraryId = "user.sounds",
            Name = dto.Name,
            Description = dto.Description,
            Category = "Personalizado",
            Accent = dto.Accent,
            IsFactory = false
        };

        foreach (var padDto in dto.Pads)
        {
            var pad = new DrumPad
            {
                Id = padDto.Id,
                Name = padDto.Name,
                ShortName = padDto.ShortName,
                Articulation = padDto.Articulation,
                Accent = padDto.Accent,
                DefaultMidiNote = padDto.DefaultMidiNote,
                ChokeGroup = padDto.ChokeGroup,
                ChokeExisting = padDto.ChokeExisting
            };

            foreach (var layerDto in padDto.Layers)
            {
                var layer = new SampleLayer
                {
                    MinVelocity = layerDto.MinVelocity,
                    MaxVelocity = layerDto.MaxVelocity,
                    Gain = layerDto.Gain
                };
                foreach (var sampleDto in layerDto.Samples.Where(sample => File.Exists(sample.Path)))
                {
                    layer.Samples.Add(new SampleReference(sampleDto.Path, sampleDto.Gain));
                }

                if (layer.Samples.Count > 0)
                {
                    pad.Layers.Add(layer);
                }
            }

            if (pad.Layers.Count > 0)
            {
                kit.Pads.Add(pad);
            }
        }

        return kit;
    }

    private static KitDto ToDto(DrumKit kit) => new()
    {
        Id = kit.Id,
        Name = kit.Name,
        Description = kit.Description,
        Accent = kit.Accent,
        Pads = kit.Pads.Select(pad => new PadDto
        {
            Id = pad.Id,
            Name = pad.Name,
            ShortName = pad.ShortName,
            Articulation = pad.Articulation,
            Accent = pad.Accent,
            DefaultMidiNote = pad.DefaultMidiNote,
            ChokeGroup = pad.ChokeGroup,
            ChokeExisting = pad.ChokeExisting,
            Layers = pad.Layers.Select(layer => new LayerDto
            {
                MinVelocity = layer.MinVelocity,
                MaxVelocity = layer.MaxVelocity,
                Gain = layer.Gain,
                Samples = layer.Samples.Select(sample => new SampleDto
                {
                    Path = sample.Path,
                    Gain = sample.Gain
                }).ToList()
            }).ToList()
        }).ToList()
    };

    private sealed class UserKitDocument
    {
        public int SchemaVersion { get; set; }
        public List<KitDto> Kits { get; set; } = [];
    }

    private sealed class KitDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Accent { get; set; } = "#A58BFF";
        public List<PadDto> Pads { get; set; } = [];
    }

    private sealed class PadDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string ShortName { get; set; } = string.Empty;
        public string Articulation { get; set; } = string.Empty;
        public string Accent { get; set; } = "#A58BFF";
        public int DefaultMidiNote { get; set; }
        public string? ChokeGroup { get; set; }
        public bool ChokeExisting { get; set; }
        public List<LayerDto> Layers { get; set; } = [];
    }

    private sealed class LayerDto
    {
        public int MinVelocity { get; set; }
        public int MaxVelocity { get; set; }
        public float Gain { get; set; } = 1f;
        public List<SampleDto> Samples { get; set; } = [];
    }

    private sealed class SampleDto
    {
        public string Path { get; set; } = string.Empty;
        public float Gain { get; set; } = 1f;
    }
}
