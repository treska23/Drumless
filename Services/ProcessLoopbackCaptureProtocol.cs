using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DrumPracticeStudio.Services;

internal static class ProcessLoopbackCaptureProtocol
{
    public const string Argument = "--process-loopback-capture";
    public const string ReadyResponse = "READY";
    private const string ErrorPrefix = "ERROR:";

    public static void Start(uint rootProcessId)
    {
        _ = Task.Run(() => RunAsync(rootProcessId))
            .ContinueWith(
                task => System.Windows.Application.Current.Dispatcher.BeginInvoke(
                    () => System.Windows.Application.Current.Shutdown(
                        task.Status == TaskStatus.RanToCompletion ? task.Result : 1)),
                TaskScheduler.Default);
    }

    public static string? DecodeError(string? response)
    {
        if (string.IsNullOrWhiteSpace(response) ||
            !response.StartsWith(ErrorPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(
                Convert.FromBase64String(response[ErrorPrefix.Length..]));
        }
        catch (FormatException)
        {
            return response;
        }
    }

    private static async Task<int> RunAsync(uint rootProcessId)
    {
        await using var output = Console.OpenStandardOutput();
        await using var errorStream = Console.OpenStandardError();
        await using var error = new StreamWriter(
            errorStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            leaveOpen: true)
        {
            AutoFlush = true
        };
        WasapiRecorder? recorder = null;
        var outputGate = new object();
        try
        {
            recorder = await new WasapiRecorderBuilder()
                .WithProcessLoopback(
                    rootProcessId,
                    ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2))
                .WithBufferLength(40)
                .WithMmcssThreadPriority("Audio")
                .BuildAsync()
                .ConfigureAwait(false);

            void OnDataAvailable(
                ReadOnlySpan<byte> buffer,
                AudioClientBufferFlags flags,
                long devicePosition,
                long qpcPosition)
            {
                if (buffer.IsEmpty ||
                    flags.HasFlag(AudioClientBufferFlags.Silent))
                {
                    return;
                }

                try
                {
                    lock (outputGate)
                    {
                        output.Write(buffer);
                    }
                }
                catch (IOException)
                {
                    // El proceso principal ha cerrado la tubería.
                }
            }

            recorder.DataAvailable += OnDataAvailable;
            await error.WriteLineAsync(ReadyResponse);
            recorder.StartRecording();
            await Console.In.ReadLineAsync();
            recorder.DataAvailable -= OnDataAvailable;
            recorder.StopRecording();
            return 0;
        }
        catch (Exception exception)
        {
            var payload = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(exception.ToString()));
            try
            {
                await error.WriteLineAsync(ErrorPrefix + payload);
            }
            catch
            {
            }
            return 1;
        }
        finally
        {
            recorder?.Dispose();
        }
    }
}
