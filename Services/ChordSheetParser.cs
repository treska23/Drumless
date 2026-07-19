using System.Text;
using System.Text.RegularExpressions;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public sealed partial class ChordSheetParser
{
    public ChordSheetDocument Parse(
        string text,
        string? title = null,
        ChordSheetSourceKind sourceKind = ChordSheetSourceKind.UserText,
        string? sourceUrl = null)
    {
        text = NormalizeLineEndings(text ?? string.Empty).Trim();
        var sourceLines = text.Split('\n');
        var lines = new List<ChordSheetLine>(sourceLines.Length);
        string? currentSection = null;
        for (var index = 0; index < sourceLines.Length; index++)
        {
            var sourceLine = sourceLines[index].TrimEnd();
            var rendered = SectionRegex().IsMatch(sourceLine.Trim())
                ? [sourceLine]
                : RenderChordProLine(sourceLine);
            foreach (var displayLine in rendered)
            {
                var kind = Classify(displayLine);
                if (kind == ChordSheetLineKind.Section)
                {
                    currentSection = CleanSectionLabel(displayLine);
                }
                lines.Add(new ChordSheetLine(
                    Guid.NewGuid().ToString("N"),
                    lines.Count,
                    kind,
                    displayLine,
                    null,
                    0d,
                    currentSection));
            }
        }

        return ChordSheetDocument.Normalize(new ChordSheetDocument(
            Guid.NewGuid().ToString("N"),
            string.IsNullOrWhiteSpace(title) ? "Letra y acordes" : title,
            sourceKind,
            sourceUrl,
            text,
            DateTimeOffset.UtcNow,
            2d,
            lines));
    }

    public static string NormalizeExtractedText(string text)
    {
        var normalized = NormalizeLineEndings(text ?? string.Empty);
        var lines = normalized.Split('\n')
            .Select(line => line.TrimEnd())
            .ToList();
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var result = new List<string>(lines.Count);
        var consecutiveEmpty = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                consecutiveEmpty++;
                if (consecutiveEmpty <= 2)
                {
                    result.Add(string.Empty);
                }
                continue;
            }
            consecutiveEmpty = 0;
            result.Add(line);
        }
        return string.Join(Environment.NewLine, result);
    }

    private static IReadOnlyList<string> RenderChordProLine(string line)
    {
        var matches = InlineChordRegex().Matches(line);
        if (matches.Count == 0)
        {
            if (DirectiveRegex().IsMatch(line))
            {
                var directive = line.Trim().Trim('{', '}');
                var separator = directive.IndexOf(':');
                var value = separator >= 0 ? directive[(separator + 1)..] : directive;
                return string.IsNullOrWhiteSpace(value) ? [] : [$"[{value.Trim()}]"];
            }
            return [line];
        }

        var lyric = new StringBuilder();
        var chord = new StringBuilder();
        var sourceIndex = 0;
        foreach (Match match in matches)
        {
            var plain = line[sourceIndex..match.Index];
            lyric.Append(plain);
            chord.Append(' ', Math.Max(0, lyric.Length - chord.Length));
            chord.Append(match.Groups[1].Value.Trim());
            sourceIndex = match.Index + match.Length;
        }
        lyric.Append(line[sourceIndex..]);
        return [chord.ToString().TrimEnd(), lyric.ToString().TrimEnd()];
    }

    private static ChordSheetLineKind Classify(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return ChordSheetLineKind.Empty;
        }
        if (SectionRegex().IsMatch(trimmed))
        {
            return ChordSheetLineKind.Section;
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var chordTokens = tokens.Count(token => ChordTokenRegex().IsMatch(
            token.Trim(',', '.', '|', ':', '(', ')')));
        if (tokens.Length > 0 && chordTokens >= Math.Max(1, (int)Math.Ceiling(tokens.Length * 0.6d)))
        {
            return ChordSheetLineKind.Chords;
        }
        if (trimmed.StartsWith('#') || trimmed.StartsWith('*'))
        {
            return ChordSheetLineKind.Annotation;
        }
        return ChordSheetLineKind.Lyrics;
    }

    private static string CleanSectionLabel(string line) =>
        line.Trim().Trim('[', ']', '(', ')', ':', '-', ' ');

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    [GeneratedRegex(@"\[([^\]\r\n]{1,24})\]")]
    private static partial Regex InlineChordRegex();

    [GeneratedRegex(@"^\s*\{(?:title|subtitle|artist|comment|start_of_|end_of_|soc|eoc|sov|eov)[^}]*\}\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex DirectiveRegex();

    [GeneratedRegex(@"^(?:\[|\()?(?:intro|verse|estrofa|chorus|coro|estribillo|bridge|puente|solo|outro|final|pre[- ]?chorus|instrumental)(?:\s+\d+)?(?:\]|\)|:|-)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^(?:[A-G](?:#|b)?(?:m|maj|min|dim|aug|sus|add)?\d*(?:/[A-G](?:#|b)?)?|N\.?C\.?)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ChordTokenRegex();
}
