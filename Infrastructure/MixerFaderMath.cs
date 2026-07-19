namespace DrumPracticeStudio.Infrastructure;

public static class MixerFaderMath
{
    public static double ValueFromVerticalPoint(
        double minimum,
        double maximum,
        double trackHeight,
        double thumbHeight,
        double pointY)
    {
        if (!double.IsFinite(minimum) ||
            !double.IsFinite(maximum) ||
            !double.IsFinite(trackHeight) ||
            !double.IsFinite(thumbHeight) ||
            !double.IsFinite(pointY) ||
            maximum <= minimum ||
            trackHeight <= 0d)
        {
            return minimum;
        }

        var boundedThumbHeight = Math.Clamp(thumbHeight, 0d, trackHeight);
        var usableHeight = Math.Max(1d, trackHeight - boundedThumbHeight);
        var thumbCenter = Math.Clamp(
            pointY - (boundedThumbHeight / 2d),
            0d,
            usableHeight);
        var ratio = 1d - (thumbCenter / usableHeight);
        return Math.Clamp(
            minimum + (ratio * (maximum - minimum)),
            minimum,
            maximum);
    }
}
