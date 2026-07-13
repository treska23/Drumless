namespace DrumPracticeStudio.Midi;

internal static class MidiVelocityCurve
{
    public const double NeutralSensitivity = 50d;
    public const double DefaultSensitivity = 72d;

    public static int Apply(int velocity, double sensitivity)
    {
        if (velocity <= 0)
        {
            return 0;
        }

        var boundedVelocity = Math.Clamp(velocity, 1, 127);
        var boundedSensitivity = Math.Clamp(sensitivity, 0d, 100d);
        var gamma = Math.Pow(2d, (NeutralSensitivity - boundedSensitivity) / 40d);
        var normalized = boundedVelocity / 127d;
        var adjusted = (int)Math.Round(
            127d * Math.Pow(normalized, gamma),
            MidpointRounding.AwayFromZero);

        return Math.Clamp(adjusted, 1, 127);
    }
}
