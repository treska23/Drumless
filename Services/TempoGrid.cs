using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

public static class TempoGrid
{
    public static double NearestGridErrorSeconds(
        double positionSeconds,
        TempoSettings tempo,
        int subdivisionsPerBeat = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(subdivisionsPerBeat, 1);
        tempo = TempoSettings.Normalize(tempo);
        var segment = tempo.GetSegmentAt(positionSeconds);
        var step = 60d / segment.Bpm / subdivisionsPerBeat;
        var relative = positionSeconds - segment.FirstBeatSeconds;
        var nearest = Math.Round(relative / step, MidpointRounding.AwayFromZero);
        return relative - nearest * step;
    }
}
