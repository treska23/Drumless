using System.Text.Json;
using System.Text.Json.Serialization;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed class StudioStateStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _statePath;

    public StudioStateStore(string? statePath = null)
    {
        var requestedPath = string.IsNullOrWhiteSpace(statePath)
            ? AppPaths.StudioStatePath
            : statePath;
        _statePath = Path.GetFullPath(requestedPath);
    }

    public string StatePath => _statePath;
    public string? LastLoadWarning { get; private set; }

    public StudioState Load()
    {
        LastLoadWarning = null;
        if (!File.Exists(_statePath))
        {
            return CreateDefaultState();
        }

        try
        {
            using var stream = File.OpenRead(_statePath);
            var document = JsonSerializer.Deserialize<StudioStateDocument>(stream, JsonOptions);
            if (document is null)
            {
                throw new JsonException("El documento de estado está vacío.");
            }

            if (document.SchemaVersion != CurrentSchemaVersion)
            {
                throw new NotSupportedException(
                    $"La versión {document.SchemaVersion} del estado no es compatible.");
            }

            return ToModel(document);
        }
        catch (Exception exception) when (IsRecoverableLoadFailure(exception))
        {
            LastLoadWarning = $"No se pudo recuperar el estado guardado: {exception.Message}";
            return CreateDefaultState();
        }
    }

    public void Save(StudioState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var directory = Path.GetDirectoryName(_statePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("La ruta del estado no tiene un directorio válido.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_statePath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 16_384,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, ToDocument(state), JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Un temporal huérfano no debe ocultar el error de guardado principal.
            }
        }
    }

    private static StudioState CreateDefaultState() => new()
    {
        OutputFolder = AppPaths.DerivedTracks,
        PlaybackMode = PlaybackMode.Sequential
    };

    private static StudioState ToModel(StudioStateDocument document)
    {
        var state = new StudioState
        {
            OutputFolder = string.IsNullOrWhiteSpace(document.OutputFolder)
                ? AppPaths.DerivedTracks
                : document.OutputFolder,
            SelectedPlaylistId = string.IsNullOrWhiteSpace(document.SelectedPlaylistId)
                ? null
                : document.SelectedPlaylistId,
            PlaybackMode = Enum.IsDefined(document.PlaybackMode)
                ? document.PlaybackMode
                : PlaybackMode.Sequential
        };

        foreach (var track in document.Tracks ?? [])
        {
            if (string.IsNullOrWhiteSpace(track.Id) ||
                string.IsNullOrWhiteSpace(track.Title) ||
                string.IsNullOrWhiteSpace(track.Path) ||
                !Enum.IsDefined(track.Variant))
            {
                continue;
            }

            state.Tracks.Add(new TrackRecord
            {
                Id = track.Id,
                Title = track.Title,
                Path = track.Path,
                Variant = track.Variant
            });
        }

        foreach (var playlist in document.Playlists ?? [])
        {
            if (string.IsNullOrWhiteSpace(playlist.Id) || string.IsNullOrWhiteSpace(playlist.Name))
            {
                continue;
            }

            var model = new Playlist { Id = playlist.Id, Name = playlist.Name };
            foreach (var trackId in playlist.TrackIds ?? [])
            {
                if (!string.IsNullOrWhiteSpace(trackId))
                {
                    model.TrackIds.Add(trackId);
                }
            }

            state.Playlists.Add(model);
        }

        return state;
    }

    private static StudioStateDocument ToDocument(StudioState state) => new()
    {
        SchemaVersion = CurrentSchemaVersion,
        OutputFolder = string.IsNullOrWhiteSpace(state.OutputFolder)
            ? AppPaths.DerivedTracks
            : state.OutputFolder,
        SelectedPlaylistId = state.SelectedPlaylistId,
        PlaybackMode = state.PlaybackMode,
        Tracks = state.Tracks.Select(track => new TrackDto
        {
            Id = track.Id,
            Title = track.Title,
            Path = track.Path,
            Variant = track.Variant
        }).ToList(),
        Playlists = state.Playlists.Select(playlist => new PlaylistDto
        {
            Id = playlist.Id,
            Name = playlist.Name,
            TrackIds = playlist.TrackIds.ToList()
        }).ToList()
    };

    private static bool IsRecoverableLoadFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        JsonException or
        NotSupportedException;

    private sealed class StudioStateDocument
    {
        public int SchemaVersion { get; set; }
        public string? OutputFolder { get; set; }
        public List<TrackDto>? Tracks { get; set; }
        public List<PlaylistDto>? Playlists { get; set; }
        public string? SelectedPlaylistId { get; set; }
        public PlaybackMode PlaybackMode { get; set; } = PlaybackMode.Sequential;
    }

    private sealed class TrackDto
    {
        public string? Id { get; set; }
        public string? Title { get; set; }
        public string? Path { get; set; }
        public TrackVariant Variant { get; set; }
    }

    private sealed class PlaylistDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<string>? TrackIds { get; set; }
    }
}
