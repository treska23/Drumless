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
        var step = 60d / tempo.Bpm / subdivisionsPerBeat;
        var relative = positionSeconds - tempo.FirstBeatSeconds;
        var nearest = Math.Round(relative / step, MidpointRounding.AwayFromZero);
        return relative - nearest * step;
    }
}
