using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed class ChordSheetAlignmentService
{
    public ChordSheetDocument Align(
        ChordSheetDocument document,
        SongStructureMap? structure,
        double durationSeconds)
    {
        ArgumentNullException.ThrowIfNull(document);
        document = ChordSheetDocument.Normalize(document);
        var duration = double.IsFinite(durationSeconds)
            ? Math.Max(0d, durationSeconds)
            : 0d;
        var sections = structure is null
            ? []
            : SongStructureMap.Normalize(structure).Sections;
        if (document.Lines.Count == 0 || duration <= 0d)
        {
            return document;
        }

        var sectionLineIndexes = document.Lines
            .Select((line, index) => (line, index))
            .Where(item => item.line.Kind == ChordSheetLineKind.Section)
            .Select(item => item.index)
            .ToArray();
        var aligned = document.Lines.ToArray();
        if (sections.Count > 0 && sectionLineIndexes.Length > 0)
        {
            for (var groupIndex = 0; groupIndex < sectionLineIndexes.Length; groupIndex++)
            {
                var startIndex = sectionLineIndexes[groupIndex];
                var endIndex = groupIndex + 1 < sectionLineIndexes.Length
                    ? sectionLineIndexes[groupIndex + 1]
                    : aligned.Length;
                var section = sections[Math.Min(groupIndex, sections.Count - 1)];
                AlignRange(aligned, startIndex, endIndex, section.StartSeconds, section.EndSeconds, 0.48d);
            }
        }
        else if (sections.Count > 0)
        {
            for (var sectionIndex = 0; sectionIndex < sections.Count; sectionIndex++)
            {
                var startIndex = (int)Math.Round(sectionIndex * aligned.Length / (double)sections.Count);
                var endIndex = (int)Math.Round((sectionIndex + 1) * aligned.Length / (double)sections.Count);
                var section = sections[sectionIndex];
                AlignRange(aligned, startIndex, endIndex, section.StartSeconds, section.EndSeconds, 0.28d);
            }
        }
        else
        {
            AlignRange(aligned, 0, aligned.Length, 0d, duration, 0.18d);
        }

        return ChordSheetDocument.Normalize(document with
        {
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Lines = aligned
        });
    }

    private static void AlignRange(
        ChordSheetLine[] lines,
        int startIndex,
        int endIndex,
        double startSeconds,
        double endSeconds,
        double confidence)
    {
        startIndex = Math.Clamp(startIndex, 0, lines.Length);
        endIndex = Math.Clamp(endIndex, startIndex, lines.Length);
        var visible = Enumerable.Range(startIndex, endIndex - startIndex)
            .Where(index => lines[index].Kind != ChordSheetLineKind.Empty)
            .ToArray();
        if (visible.Length == 0)
        {
            return;
        }

        var span = Math.Max(0d, endSeconds - startSeconds);
        for (var order = 0; order < visible.Length; order++)
        {
            var index = visible[order];
            if (lines[index].StartSeconds is not null && lines[index].Confidence >= 0.95d)
            {
                continue;
            }
            var relative = visible.Length == 1 ? 0d : order / (double)visible.Length;
            lines[index] = lines[index] with
            {
                StartSeconds = Math.Max(0d, startSeconds + (span * relative)),
                Confidence = confidence
            };
        }
    }
}
