using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed record DrumLibraryImportResult(DrumKit Kit, int ImportedFiles, int SkippedFiles);

public sealed class DrumLibraryImportService
{
    private static readonly PadDefinition[] PadDefinitions =
    [
        new("kick.main", "Bombo", "KICK", 36, "#FFB548", null, false),
        new("snare.center", "Caja", "SNARE", 38, "#FF6B72", null, false),
        new("hihat.closed", "Charles cerrado", "HH CLOSED", 42, "#62D3A4", "hihat", true),
        new("hihat.open", "Charles abierto", "HH OPEN", 46, "#51B9D7", "hihat", true),
        new("tom.low", "Tom grave", "LOW TOM", 45, "#A58BFF", null, false),
        new("tom.high", "Tom agudo", "HIGH TOM", 48, "#C783FF", null, false),
        new("crash.edge", "Crash", "CRASH", 49, "#F7D66A", "crash", false),
        new("ride.bow", "Ride", "RIDE", 51, "#F2A65A", "ride", false)
    ];

    public DrumLibraryImportResult ImportFolder(
        string folderPath,
        Func<string, string>? importSample = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (!Directory.Exists(folderPath))
        {
            throw new DirectoryNotFoundException($"No existe la carpeta {folderPath}.");
        }

        importSample ??= static path => path;
        var wavFiles = Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (wavFiles.Length == 0)
        {
            throw new InvalidDataException("La carpeta no contiene archivos WAV.");
        }

        var recognized = new List<RecognizedSample>();
        foreach (var file in wavFiles)
        {
            var searchableName = Path.GetRelativePath(folderPath, file);
            if (TryRecognize(searchableName, out var articulation, out var velocity))
            {
                recognized.Add(new RecognizedSample(file, articulation, velocity));
            }
        }

        if (recognized.Count == 0)
        {
            throw new InvalidDataException(
                "No se reconoció ningún instrumento. Usa nombres como kick, snare, hihat-closed, " +
                "hihat-open, tom-high, tom-low, crash o ride.");
        }

        var kitName = new DirectoryInfo(folderPath).Name;
        var kit = new DrumKit
        {
            Id = $"user.imported.{Guid.NewGuid():N}",
            LibraryId = "user.sounds",
            Name = kitName,
            Description = $"Kit importado desde {kitName} · {recognized.Count} muestras WAV.",
            Category = "Importado",
            Accent = "#A58BFF",
            IsFactory = false
        };

        foreach (var definition in PadDefinitions)
        {
            var samples = recognized
                .Where(sample => sample.Articulation == definition.Id)
                .ToArray();
            if (samples.Length == 0)
            {
                continue;
            }

            var pad = CreatePad(definition);
            AddVelocityLayers(pad, samples, importSample);
            kit.Pads.Add(pad);
        }

        return new DrumLibraryImportResult(kit, recognized.Count, wavFiles.Length - recognized.Count);
    }

    private static DrumPad CreatePad(PadDefinition definition) => new()
    {
        Id = definition.Id,
        Name = definition.Name,
        ShortName = definition.ShortName,
        Articulation = definition.Id,
        Accent = definition.Accent,
        DefaultMidiNote = definition.Note,
        ChokeGroup = definition.ChokeGroup,
        ChokeExisting = definition.ChokeExisting
    };

    private static void AddVelocityLayers(
        DrumPad pad,
        IReadOnlyCollection<RecognizedSample> samples,
        Func<string, string> importSample)
    {
        var hasVelocityNames = samples.Any(sample => sample.Velocity is not null);
        var groups = samples
            .GroupBy(sample => hasVelocityNames ? sample.Velocity ?? 80 : 64)
            .OrderBy(group => group.Key)
            .ToArray();

        for (var index = 0; index < groups.Length; index++)
        {
            var minimum = index == 0
                ? 1
                : ((groups[index - 1].Key + groups[index].Key) / 2) + 1;
            var maximum = index == groups.Length - 1
                ? 127
                : (groups[index].Key + groups[index + 1].Key) / 2;
            var layer = new SampleLayer
            {
                MinVelocity = minimum,
                MaxVelocity = maximum,
                Gain = 0.9f
            };

            foreach (var sample in groups[index])
            {
                layer.Samples.Add(new SampleReference(importSample(sample.Path)));
            }

            pad.Layers.Add(layer);
        }
    }

    private static bool TryRecognize(string path, out string articulation, out int? velocity)
    {
        var name = Normalize(path);
        var tokens = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        articulation = RecognizeArticulation(tokens);
        velocity = RecognizeVelocity(name, tokens);
        return articulation.Length > 0;
    }

    private static string RecognizeArticulation(HashSet<string> tokens)
    {
        if (HasAny(tokens, "kick", "kicks", "bombo", "bombos", "bd", "bassdrum"))
        {
            return "kick.main";
        }

        if (HasAny(tokens, "snare", "snares", "caja", "cajas", "sd"))
        {
            return "snare.center";
        }

        var isHiHat = HasAny(tokens, "hihat", "hihats", "hh", "hat", "hats", "charles");
        if ((isHiHat && HasAny(tokens, "closed", "close", "cerrado", "cerrados", "chh")) ||
            HasAny(tokens, "hihatclosed", "closedhat", "clhat"))
        {
            return "hihat.closed";
        }

        if ((isHiHat && HasAny(tokens, "open", "opened", "abierto", "abiertos", "ohh")) ||
            HasAny(tokens, "hihatopen", "openhat", "opnhat"))
        {
            return "hihat.open";
        }

        if (HasAny(tokens, "crash", "crashes"))
        {
            return "crash.edge";
        }

        if (HasAny(tokens, "ride", "rides"))
        {
            return "ride.bow";
        }

        var isTom = tokens.Contains("tom") || HasAny(tokens, "tom1", "tom2", "tom3", "tomhigh", "tomlow");
        if (isTom && HasAny(tokens, "low", "floor", "grave", "bajo", "tom2", "tom3", "tomlow"))
        {
            return "tom.low";
        }

        if (isTom && HasAny(tokens, "high", "rack", "agudo", "alto", "tom1", "tomhigh"))
        {
            return "tom.high";
        }

        return string.Empty;
    }

    private static int? RecognizeVelocity(string name, HashSet<string> tokens)
    {
        var match = Regex.Match(name, @"(?:^|\s)(?:v|vel|velocity)\s*(\d{1,3})(?:\s|$)");
        if (match.Success &&
            int.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var numericVelocity))
        {
            return Math.Clamp(numericVelocity, 1, 127);
        }

        if (HasAny(tokens, "soft", "suave", "piano", "pp", "p"))
        {
            return 32;
        }

        if (HasAny(tokens, "medium", "mediumsoft", "mid", "medio", "mp", "mf"))
        {
            return 80;
        }

        if (HasAny(tokens, "hard", "loud", "fuerte", "forte", "ff", "f"))
        {
            return 112;
        }

        return null;
    }

    private static bool HasAny(HashSet<string> tokens, params string[] candidates) =>
        candidates.Any(tokens.Contains);

    private static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            result.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : ' ');
        }

        return Regex.Replace(result.ToString(), @"\s+", " ").Trim();
    }

    private sealed record RecognizedSample(string Path, string Articulation, int? Velocity);

    private sealed record PadDefinition(
        string Id,
        string Name,
        string ShortName,
        int Note,
        string Accent,
        string? ChokeGroup,
        bool ChokeExisting);
}
