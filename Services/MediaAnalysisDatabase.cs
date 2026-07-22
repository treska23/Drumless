using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed class MediaAnalysisDatabase
{
    private readonly Dictionary<string, MediaAnalysisRecord> _records =
        new(StringComparer.Ordinal);

    public void Load(IEnumerable<MediaAnalysisRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        _records.Clear();
        foreach (var record in records)
        {
            if (record is null || string.IsNullOrWhiteSpace(record.MediaKey) ||
                _records.ContainsKey(record.MediaKey))
            {
                continue;
            }

            _records.Add(record.MediaKey, Clone(record));
        }
    }

    public MediaAnalysisRecord? Get(string? mediaKey) =>
        string.IsNullOrWhiteSpace(mediaKey) || !_records.TryGetValue(mediaKey, out var record)
            ? null
            : record;

    public TempoSettings? GetTempo(string? mediaKey) => Get(mediaKey)?.Tempo;

    public void ImportTempoIfMissing(string mediaKey, TempoSettings? tempo)
    {
        if (tempo is null || string.IsNullOrWhiteSpace(mediaKey) ||
            _records.ContainsKey(mediaKey))
        {
            return;
        }

        SetTempo(mediaKey, tempo, TempoAnalysisOrigin.Manual, DateTimeOffset.UtcNow);
    }

    public void SetTempo(
        string mediaKey,
        TempoSettings tempo,
        TempoAnalysisOrigin origin,
        DateTimeOffset? updatedAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaKey);
        ArgumentNullException.ThrowIfNull(tempo);
        var record = GetOrCreate(mediaKey);
        record.Tempo = TempoSettings.Normalize(tempo);
        record.TempoOrigin = origin;
        record.TempoUpdatedAtUtc = updatedAtUtc ?? DateTimeOffset.UtcNow;
    }

    public void AddPerformanceSession(string mediaKey, DrumPerformanceSession session)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaKey);
        ArgumentNullException.ThrowIfNull(session);
        var record = GetOrCreate(mediaKey);
        if (record.PerformanceSessions.All(existing => existing.Id != session.Id))
        {
            record.PerformanceSessions.Add(session);
        }
    }

    public void SetDrumReference(string mediaKey, DrumReferenceMap? reference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaKey);
        var record = GetOrCreate(mediaKey);
        record.DrumReference = reference is null
            ? null
            : DrumReferenceMap.Normalize(reference);
    }

    public void SetSongStructure(string mediaKey, SongStructureMap? structure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaKey);
        var record = GetOrCreate(mediaKey);
        record.SongStructure = structure is null
            ? null
            : SongStructureMap.Normalize(structure);
    }

    public void SetChordSheet(string mediaKey, ChordSheetDocument? chordSheet)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaKey);
        var record = GetOrCreate(mediaKey);
        record.ChordSheet = chordSheet is null
            ? null
            : ChordSheetDocument.Normalize(chordSheet);
    }

    public void SetSongEffectProfile(string mediaKey, SongEffectProfile? profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaKey);
        var record = GetOrCreate(mediaKey);
        record.SongEffectProfile = profile;
    }

    public bool Remove(string? mediaKey) =>
        !string.IsNullOrWhiteSpace(mediaKey) && _records.Remove(mediaKey);

    public IReadOnlyList<MediaAnalysisRecord> Snapshot() => _records.Values
        .OrderBy(record => record.MediaKey, StringComparer.Ordinal)
        .Select(Clone)
        .ToArray();

    private MediaAnalysisRecord GetOrCreate(string mediaKey)
    {
        if (_records.TryGetValue(mediaKey, out var record))
        {
            return record;
        }

        record = new MediaAnalysisRecord { MediaKey = mediaKey };
        _records.Add(mediaKey, record);
        return record;
    }

    private static MediaAnalysisRecord Clone(MediaAnalysisRecord record) => new()
    {
        MediaKey = record.MediaKey,
        Tempo = record.Tempo is null ? null : TempoSettings.Normalize(record.Tempo),
        TempoOrigin = record.TempoOrigin,
        TempoUpdatedAtUtc = record.TempoUpdatedAtUtc,
        SongStructure = record.SongStructure is null
            ? null
            : SongStructureMap.Normalize(record.SongStructure),
        ChordSheet = record.ChordSheet is null
            ? null
            : ChordSheetDocument.Normalize(record.ChordSheet),
        DrumReference = record.DrumReference is null
            ? null
            : DrumReferenceMap.Normalize(record.DrumReference),
        SongEffectProfile = record.SongEffectProfile,
        PerformanceSessions = record.PerformanceSessions.ToList()
    };
}
