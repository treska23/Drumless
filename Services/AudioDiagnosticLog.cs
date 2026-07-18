using System.Text;
using DrumPracticeStudio.Audio;

namespace DrumPracticeStudio.Services;

public static class AudioDiagnosticLog
{
    private static readonly object Sync = new();

    public static void Append(AudioOutputFault fault, string? recovery = null)
    {
        ArgumentNullException.ThrowIfNull(fault);
        try
        {
            var directory = Path.GetDirectoryName(AppPaths.AudioDiagnosticsPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var line = new StringBuilder()
                .Append(fault.OccurredAtUtc.ToString("O"))
                .Append(" | ").Append(fault.Backend)
                .Append(" | ").Append(fault.DeviceName)
                .Append(" | ").Append(fault.ExceptionType)
                .Append(" | ").Append(fault.ErrorCode)
                .Append(" | ").Append(fault.Message.ReplaceLineEndings(" "));
            if (!string.IsNullOrWhiteSpace(recovery))
            {
                line.Append(" | ").Append(recovery.ReplaceLineEndings(" "));
            }

            lock (Sync)
            {
                File.AppendAllText(
                    AppPaths.AudioDiagnosticsPath,
                    line.AppendLine().ToString(),
                    Encoding.UTF8);
            }
        }
        catch
        {
            // El registro nunca debe provocar un segundo fallo en el motor de audio.
        }
    }
}
