namespace DrumPracticeStudio.Models;

public sealed class ChordSheetViewportMarkerItem
{
    public ChordSheetViewportMarkerItem(
        ChordSheetViewportMarker marker,
        string lineText)
    {
        Id = marker.Id;
        Seconds = marker.Seconds;
        LineId = marker.LineId;
        LineText = string.IsNullOrWhiteSpace(lineText) ? "Línea sin texto" : lineText.Trim();
    }

    public string Id { get; }
    public double Seconds { get; }
    public string LineId { get; }
    public string LineText { get; }

    public string TimeLabel
    {
        get
        {
            var time = TimeSpan.FromSeconds(Seconds);
            return time.TotalHours >= 1d
                ? time.ToString(@"h\:mm\:ss")
                : time.ToString(@"m\:ss");
        }
    }

    public string Summary => $"{TimeLabel} · {LineText}";

    public ChordSheetViewportMarker ToModel() => new(Id, Seconds, LineId);
}
