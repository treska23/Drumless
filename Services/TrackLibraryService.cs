using System.Collections.ObjectModel;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed class TrackLibraryService
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav",
        ".mp3",
        ".flac",
        ".aiff",
        ".aif",
        ".m4a"
    };

    private readonly Dictionary<string, LocalTrack> _tracksByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LocalTrack> _tracksById = new(StringComparer.Ordinal);

    public TrackLibraryService(IEnumerable<TrackRecord>? records = null)
    {
        if (records is not null)
        {
            Load(records);
        }
    }

    public ObservableCollection<LocalTrack> Tracks { get; } = [];

    public void Load(IEnumerable<TrackRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        Tracks.Clear();
        _tracksByPath.Clear();
        _tracksById.Clear();

        foreach (var record in records)
        {
            if (record is null ||
                string.IsNullOrWhiteSpace(record.Id) ||
                string.IsNullOrWhiteSpace(record.Title) ||
                string.IsNullOrWhiteSpace(record.Path) ||
                !TryNormalizePath(record.Path, out var normalizedPath) ||
                _tracksById.ContainsKey(record.Id) ||
                _tracksByPath.ContainsKey(normalizedPath))
            {
                continue;
            }

            AddTrack(new LocalTrack
            {
                Id = record.Id,
                Title = record.Title,
                Path = normalizedPath,
                Variant = record.Variant,
                IsMissing = !File.Exists(normalizedPath)
            });
        }
    }

    public IReadOnlyList<TrackRecord> Snapshot() => Tracks
        .Select(track => new TrackRecord
        {
            Id = track.Id,
            Title = track.Title,
            Path = track.Path,
            Variant = track.Variant
        })
        .ToArray();

    public IReadOnlyList<LocalTrack> ScanFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        RefreshAvailability();

        var normalizedFolder = Path.GetFullPath(folder);
        if (!Directory.Exists(normalizedFolder))
        {
            return [];
        }

        var paths = EnumerateAudioFiles(normalizedFolder)
            .Select(NormalizePath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var added = new List<LocalTrack>();

        foreach (var path in paths)
        {
            if (_tracksByPath.TryGetValue(path, out var existing))
            {
                existing.IsMissing = false;
                continue;
            }

            added.Add(RegisterGenerated(path));
        }

        return added;
    }

    public LocalTrack RegisterImported(
        string path,
        TrackVariant variant,
        string? title = null)
    {
        if (variant == TrackVariant.GeneratedDrumless)
        {
            throw new ArgumentException(
                "Una pista importada debe ser original o una pista drumless externa.",
                nameof(variant));
        }

        return Register(path, title, variant);
    }

    public LocalTrack RegisterGenerated(string path, string? title = null) =>
        Register(path, title, TrackVariant.GeneratedDrumless);

    public void RefreshAvailability()
    {
        foreach (var track in Tracks)
        {
            track.IsMissing = !File.Exists(track.Path);
        }
    }

    public bool TryGetById(string trackId, out LocalTrack track) =>
        _tracksById.TryGetValue(trackId, out track!);

    public static string NormalizePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private LocalTrack Register(string path, string? title, TrackVariant variant)
    {
        var normalizedPath = NormalizePath(path);
        if (_tracksByPath.TryGetValue(normalizedPath, out var existing))
        {
            existing.IsMissing = !File.Exists(normalizedPath);
            return existing;
        }

        var resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(normalizedPath)
            : title.Trim();
        var track = new LocalTrack
        {
            Id = CreateTrackId(),
            Title = resolvedTitle,
            Path = normalizedPath,
            Variant = variant,
            IsMissing = !File.Exists(normalizedPath)
        };
        AddTrack(track);
        return track;
    }

    private void AddTrack(LocalTrack track)
    {
        Tracks.Add(track);
        _tracksByPath.Add(track.Path, track);
        _tracksById.Add(track.Id, track);
    }

    private string CreateTrackId()
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (_tracksById.ContainsKey(id));

        return id;
    }

    private static bool TryNormalizePath(string path, out string normalizedPath)
    {
        try
        {
            normalizedPath = NormalizePath(path);
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            normalizedPath = string.Empty;
            return false;
        }
    }

    private static IEnumerable<string> EnumerateAudioFiles(string root)
    {
        var rootInfo = new DirectoryInfo(root);
        if (string.Equals(rootInfo.Name, ".work", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.TryPop(out var current))
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(current);
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                if (SupportedExtensions.Contains(Path.GetExtension(file)))
                {
                    yield return file;
                }
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(current);
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in directories)
            {
                if (string.Equals(Path.GetFileName(directory), ".work", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (exception is
                    IOException or
                    UnauthorizedAccessException)
                {
                    continue;
                }

                pending.Push(directory);
            }
        }
    }
}
