using DrumPracticeStudio.Infrastructure;

namespace DrumPracticeStudio.Models;

public sealed class ChordSheetLineItem : ObservableObject
{
    private double? _startSeconds;
    private bool _isCurrent;
    private bool _isViewSwitchTarget;
    private string _viewSwitchLabel = string.Empty;

    public ChordSheetLineItem(ChordSheetLine line)
    {
        Id = line.Id;
        Order = line.Order;
        Kind = line.Kind;
        Text = line.Text;
        _startSeconds = line.StartSeconds;
        Confidence = line.Confidence;
        SectionLabel = line.SectionLabel;
    }

    public string Id { get; }
    public int Order { get; }
    public ChordSheetLineKind Kind { get; }
    public string Text { get; }
    public double Confidence { get; private set; }
    public string? SectionLabel { get; }

    public double? StartSeconds
    {
        get => _startSeconds;
        set
        {
            if (SetProperty(ref _startSeconds, value))
            {
                OnPropertyChanged(nameof(TimeLabel));
            }
        }
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => SetProperty(ref _isCurrent, value);
    }

    public bool IsViewSwitchTarget
    {
        get => _isViewSwitchTarget;
        private set => SetProperty(ref _isViewSwitchTarget, value);
    }

    public string ViewSwitchLabel
    {
        get => _viewSwitchLabel;
        private set => SetProperty(ref _viewSwitchLabel, value);
    }

    public string TimeLabel => StartSeconds is { } seconds
        ? TimeSpan.FromSeconds(seconds).ToString(@"mm\:ss\.f")
        : "--:--";

    public void SetManualStart(double seconds)
    {
        StartSeconds = Math.Max(0d, seconds);
        Confidence = 1d;
    }

    public void SetViewSwitchTarget(bool isTarget, double? seconds)
    {
        IsViewSwitchTarget = isTarget;
        ViewSwitchLabel = isTarget
            ? $"CAMBIO DE VISTA · {Services.ChordSheetViewportPolicy.FormatTimestamp(seconds)}"
            : string.Empty;
    }

    public ChordSheetLine ToModel(double? startSeconds = null, double? confidence = null) =>
        new(
            Id,
            Order,
            Kind,
            Text,
            startSeconds ?? StartSeconds,
            confidence ?? Confidence,
            SectionLabel);
}
