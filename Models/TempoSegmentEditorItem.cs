using DrumPracticeStudio.Infrastructure;

namespace DrumPracticeStudio.Models;

public sealed class TempoSegmentEditorItem : ObservableObject
{
    private double _startSeconds;
    private double _bpm = 120d;
    private double _firstBeatSeconds;
    private int _beatsPerBar = 4;
    private double _confidence;
    private string _sourceName = string.Empty;
    private string? _sourceUrl;

    public TempoSegmentEditorItem(TempoSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        var normalized = TempoSegment.Normalize(segment);
        Id = normalized.Id;
        _startSeconds = normalized.StartSeconds;
        _bpm = normalized.Bpm;
        _firstBeatSeconds = normalized.FirstBeatSeconds;
        _beatsPerBar = normalized.BeatsPerBar;
        _confidence = normalized.Confidence;
        _sourceName = normalized.SourceName;
        _sourceUrl = normalized.SourceUrl;
    }

    public string Id { get; }

    public double StartSeconds
    {
        get => _startSeconds;
        set
        {
            if (SetProperty(ref _startSeconds, Math.Max(0d, value)))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public double Bpm
    {
        get => _bpm;
        set
        {
            if (SetProperty(ref _bpm, Math.Clamp(value, 40d, 240d)))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public double FirstBeatSeconds
    {
        get => _firstBeatSeconds;
        set => SetProperty(ref _firstBeatSeconds, Math.Max(0d, value));
    }

    public int BeatsPerBar
    {
        get => _beatsPerBar;
        set
        {
            if (SetProperty(ref _beatsPerBar, Math.Clamp(value, 1, 12)))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public double Confidence
    {
        get => _confidence;
        set => SetProperty(ref _confidence, Math.Clamp(value, 0d, 1d));
    }

    public string SourceName
    {
        get => _sourceName;
        set
        {
            if (SetProperty(ref _sourceName, (value ?? string.Empty).Trim()))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string? SourceUrl
    {
        get => _sourceUrl;
        set => SetProperty(ref _sourceUrl, value);
    }

    public string Summary =>
        $"{StartSeconds:0.000} s · {Bpm:0.##} BPM · {BeatsPerBar}/4" +
        (string.IsNullOrWhiteSpace(SourceName) ? string.Empty : $" · {SourceName}");

    public TempoSegment ToModel() => TempoSegment.Normalize(new TempoSegment(
        Id,
        StartSeconds,
        Bpm,
        FirstBeatSeconds,
        BeatsPerBar,
        Confidence,
        SourceName,
        SourceUrl));

}
