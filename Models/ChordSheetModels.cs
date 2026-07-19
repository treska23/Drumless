namespace DrumPracticeStudio.Models;

public enum ChordSheetSourceKind
{
    UserText,
    ImportedFile,
    WebSelection
}

public enum ChordSheetLineKind
{
    Empty,
    Section,
    Chords,
    Lyrics,
    Annotation
}

public sealed record ChordSheetSourceCandidate(
    string Id,
    string Title,
    string SourceName,
    string SourceUrl,
    string Evidence,
    double Confidence);

public sealed record SongSection(
    string Id,
    double StartSeconds,
    double EndSeconds,
    string Label,
    double Confidence,
    string Signature)
{
    public static SongSection Normalize(SongSection section, double durationSeconds)
    {
        var duration = double.IsFinite(durationSeconds)
            ? Math.Max(0d, durationSeconds)
            : 0d;
        var start = double.IsFinite(section.StartSeconds)
            ? Math.Clamp(section.StartSeconds, 0d, duration)
            : 0d;
        var end = double.IsFinite(section.EndSeconds)
            ? Math.Clamp(section.EndSeconds, start, duration)
            : duration;
        return section with
        {
            Id = string.IsNullOrWhiteSpace(section.Id)
                ? Guid.NewGuid().ToString("N")
                : section.Id,
            StartSeconds = start,
            EndSeconds = end,
            Label = string.IsNullOrWhiteSpace(section.Label)
                ? "Sección"
                : section.Label.Trim(),
            Confidence = double.IsFinite(section.Confidence)
                ? Math.Clamp(section.Confidence, 0d, 1d)
                : 0d,
            Signature = (section.Signature ?? string.Empty).Trim()
        };
    }
}

public sealed record SongStructureMap(
    DateTimeOffset AnalyzedAtUtc,
    double DurationSeconds,
    double Confidence,
    IReadOnlyList<SongSection> Sections)
{
    public static SongStructureMap Normalize(SongStructureMap map)
    {
        var duration = double.IsFinite(map.DurationSeconds)
            ? Math.Max(0d, map.DurationSeconds)
            : 0d;
        var sections = (map.Sections ?? [])
            .Where(section => section is not null)
            .Select(section => SongSection.Normalize(section, duration))
            .Where(section => section.EndSeconds > section.StartSeconds)
            .OrderBy(section => section.StartSeconds)
            .Take(128)
            .ToArray();
        return map with
        {
            AnalyzedAtUtc = map.AnalyzedAtUtc == default
                ? DateTimeOffset.UtcNow
                : map.AnalyzedAtUtc,
            DurationSeconds = duration,
            Confidence = double.IsFinite(map.Confidence)
                ? Math.Clamp(map.Confidence, 0d, 1d)
                : 0d,
            Sections = sections
        };
    }
}

public sealed record ChordSheetLine(
    string Id,
    int Order,
    ChordSheetLineKind Kind,
    string Text,
    double? StartSeconds,
    double Confidence,
    string? SectionLabel = null)
{
    public static ChordSheetLine Normalize(ChordSheetLine line, int fallbackOrder)
    {
        double? start = line.StartSeconds is { } value && double.IsFinite(value)
            ? Math.Max(0d, value)
            : null;
        return line with
        {
            Id = string.IsNullOrWhiteSpace(line.Id)
                ? Guid.NewGuid().ToString("N")
                : line.Id,
            Order = line.Order >= 0 ? line.Order : fallbackOrder,
            Kind = Enum.IsDefined(line.Kind) ? line.Kind : ChordSheetLineKind.Lyrics,
            Text = line.Text ?? string.Empty,
            StartSeconds = start,
            Confidence = double.IsFinite(line.Confidence)
                ? Math.Clamp(line.Confidence, 0d, 1d)
                : 0d,
            SectionLabel = string.IsNullOrWhiteSpace(line.SectionLabel)
                ? null
                : line.SectionLabel.Trim()
        };
    }
}

public sealed record ChordSheetDocument(
    string Id,
    string Title,
    ChordSheetSourceKind SourceKind,
    string? SourceUrl,
    string RawText,
    DateTimeOffset UpdatedAtUtc,
    double LeadSeconds,
    IReadOnlyList<ChordSheetLine> Lines)
{
    public static ChordSheetDocument Normalize(ChordSheetDocument document)
    {
        var lines = (document.Lines ?? [])
            .Where(line => line is not null)
            .Select(ChordSheetLine.Normalize)
            .OrderBy(line => line.Order)
            .Take(20_000)
            .ToArray();
        return document with
        {
            Id = string.IsNullOrWhiteSpace(document.Id)
                ? Guid.NewGuid().ToString("N")
                : document.Id,
            Title = string.IsNullOrWhiteSpace(document.Title)
                ? "Letra y acordes"
                : document.Title.Trim(),
            SourceKind = Enum.IsDefined(document.SourceKind)
                ? document.SourceKind
                : ChordSheetSourceKind.UserText,
            SourceUrl = Uri.TryCreate(document.SourceUrl, UriKind.Absolute, out var sourceUri) &&
                        sourceUri.Scheme is "http" or "https"
                ? sourceUri.AbsoluteUri
                : null,
            RawText = document.RawText ?? string.Empty,
            UpdatedAtUtc = document.UpdatedAtUtc == default
                ? DateTimeOffset.UtcNow
                : document.UpdatedAtUtc,
            LeadSeconds = double.IsFinite(document.LeadSeconds)
                ? Math.Clamp(document.LeadSeconds, -10d, 20d)
                : 2d,
            Lines = lines
        };
    }
}
