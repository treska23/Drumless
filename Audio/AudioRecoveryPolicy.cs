namespace DrumPracticeStudio.Audio;

internal static class AudioRecoveryPolicy
{
    public static int GetAutomaticAttemptCount(string? backend) =>
        string.Equals(backend, "ASIO", StringComparison.OrdinalIgnoreCase)
            ? 0
            : 1;
}
