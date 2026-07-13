using DrumPracticeStudio.Models;
using NAudio.Vst3;

namespace DrumPracticeStudio.Services;

internal sealed class Vst3PresetDiscoveryService
{
    private const int MaximumFilesPerRoot = 2_000;
    private readonly IReadOnlyList<string> _presetRoots;

    public Vst3PresetDiscoveryService(IEnumerable<string>? presetRoots = null)
    {
        _presetRoots = (presetRoots ?? GetStandardPresetRoots())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string? FindCompatiblePreset(Vst3InstrumentItem instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);

        return _presetRoots
            .SelectMany(EnumeratePresetFiles)
            .Where(path => HasClassId(path, instrument.PluginClass.ClassId))
            .OrderByDescending(path => NameScore(path, instrument.DisplayName))
            .ThenByDescending(GetLastWriteTimeUtcSafe)
            .FirstOrDefault();
    }

    private static IEnumerable<string> GetStandardPresetRoots()
    {
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "VST3 Presets");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VST3 Presets");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "VST3 Presets");
        yield return Path.Combine(AppContext.BaseDirectory, "VST3 Presets");
    }

    private static IEnumerable<string> EnumeratePresetFiles(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        var yielded = 0;
        while (pending.Count > 0 && yielded < MaximumFilesPerRoot)
        {
            var directory = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(directory, "*.vstpreset", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
                yielded++;
                if (yielded >= MaximumFilesPerRoot)
                {
                    yield break;
                }
            }

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                try
                {
                    if ((File.GetAttributes(subdirectory) & FileAttributes.ReparsePoint) == 0)
                    {
                        pending.Push(subdirectory);
                    }
                }
                catch (Exception exception) when (exception is
                    IOException or
                    UnauthorizedAccessException)
                {
                    // La carpeta cambió durante el escaneo.
                }
            }
        }
    }

    private static bool HasClassId(string path, string classId)
    {
        try
        {
            return string.Equals(
                Vst3Preset.ReadClassId(path),
                classId,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is
            IOException or
            UnauthorizedAccessException or
            InvalidDataException or
            ArgumentException)
        {
            return false;
        }
    }

    private static int NameScore(string path, string instrumentName)
    {
        var searchable = $"{path} {instrumentName}";
        var score = 0;
        if (searchable.Contains("studio", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("natural", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("acoustic", StringComparison.OrdinalIgnoreCase))
        {
            score += 4;
        }
        if (searchable.Contains("kit", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("rock", StringComparison.OrdinalIgnoreCase))
        {
            score += 2;
        }
        if (searchable.Contains("empty", StringComparison.OrdinalIgnoreCase) ||
            searchable.Contains("init", StringComparison.OrdinalIgnoreCase))
        {
            score -= 8;
        }
        return score;
    }

    private static DateTime GetLastWriteTimeUtcSafe(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
