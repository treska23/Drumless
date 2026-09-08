using DrumPracticeStudio.Models;
using NAudio.Wave;

namespace DrumPracticeStudio.Audio;

internal sealed class OutputEndpointCaptureSession : IDisposable
{
    private readonly List<Candidate> _candidates;
    private bool _started;
    private bool _stopped;
    private bool _disposed;

    private OutputEndpointCaptureSession(List<Candidate> candidates)
    {
        _candidates = candidates;
    }

    public IReadOnlyList<string> Paths => _candidates.Select(candidate => candidate.Path).ToArray();

    public static OutputEndpointCaptureSession Prepare(
        IReadOnlyList<AudioOutputDeviceItem> devices,
        string workDirectory)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDirectory);
        if (devices.Count == 0)
        {
            throw new InvalidOperationException(
                "No hay ningún endpoint WASAPI asociado a la salida seleccionada.");
        }

        Directory.CreateDirectory(workDirectory);
        var candidates = new List<Candidate>();
        var failures = new List<string>();
        foreach (var device in devices.DistinctBy(device => device.Id))
        {
            var path = System.IO.Path.Combine(
                workDirectory,
                $"endpoint-{candidates.Count + 1}-{Guid.NewGuid():N}.wav");
            OutputEndpointLoopbackRecorder? recorder = null;
            try
            {
                recorder = OutputEndpointLoopbackRecorder.Prepare(device.Id, path);
                candidates.Add(new Candidate(device, path, recorder));
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                ArgumentException or
                System.Runtime.InteropServices.COMException)
            {
                recorder?.Dispose();
                TryDelete(path);
                failures.Add($"{device.Name}: {exception.Message}");
            }
        }

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No se pudo abrir la captura de la salida física" +
                (failures.Count == 0 ? "." : $": {string.Join(" | ", failures)}"));
        }

        return new OutputEndpointCaptureSession(candidates);
    }

    public void Start(long timelineStartTimestamp)
    {
        if (_started || _stopped)
        {
            throw new InvalidOperationException("La captura de salida ya se inició o terminó.");
        }

        var failures = new List<string>();
        for (var index = _candidates.Count - 1; index >= 0; index--)
        {
            var candidate = _candidates[index];
            try
            {
                candidate.Recorder.Start(timelineStartTimestamp);
            }
            catch (Exception exception) when (exception is
                IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                ArgumentException or
                System.Runtime.InteropServices.COMException)
            {
                candidate.Recorder.Dispose();
                TryDelete(candidate.Path);
                _candidates.RemoveAt(index);
                failures.Add($"{candidate.Device.Name}: {exception.Message}");
            }
        }

        if (_candidates.Count == 0)
        {
            throw new InvalidOperationException(
                "No se pudo iniciar la captura de la salida física" +
                (failures.Count == 0 ? "." : $": {string.Join(" | ", failures)}"));
        }

        _started = true;
    }

    public async Task<OutputEndpointCaptureResult> StopAndSelectAsync(
        CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            throw new InvalidOperationException("La captura de salida no se inició.");
        }
        if (_stopped)
        {
            throw new InvalidOperationException("La captura de salida ya se detuvo.");
        }
        _stopped = true;

        var warnings = new List<string>();
        foreach (var candidate in _candidates)
        {
            candidate.Recorder.Stop();
            if (candidate.Recorder.Failure is { } failure)
            {
                warnings.Add($"{candidate.Device.Name}: {failure.Message}");
            }
            candidate.HadPayload = candidate.Recorder.HasCapturedPayload;
            candidate.Recorder.Dispose();
        }

        var ranked = new List<(Candidate Candidate, double Energy)>();
        foreach (var candidate in _candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var energy = await MeasureEnergyAsync(candidate.Path, cancellationToken);
            ranked.Add((candidate, energy));
        }

        var best = ranked
            .OrderByDescending(item => item.Energy)
            .ThenByDescending(item => item.Candidate.HadPayload)
            .First();

        return new OutputEndpointCaptureResult(
            best.Candidate.Path,
            best.Candidate.Device,
            best.Energy,
            warnings.Count == 0 ? null : string.Join(" | ", warnings),
            _candidates.Select(candidate => candidate.Path).ToArray());
    }

    private static async Task<double> MeasureEnergyAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return 0d;
        }

        return await Task.Run(() =>
        {
            using var reader = new AudioFileReader(path);
            var buffer = new float[8_192];
            double sumSquares = 0d;
            long samples = 0;
            int read;
            while ((read = reader.Read(buffer.AsSpan())) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var index = 0; index < read; index++)
                {
                    var sample = buffer[index];
                    sumSquares += sample * sample;
                }
                samples += read;
            }

            return samples == 0 ? 0d : Math.Sqrt(sumSquares / samples);
        }, cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        foreach (var candidate in _candidates)
        {
            candidate.Recorder.Dispose();
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class Candidate(
        AudioOutputDeviceItem device,
        string path,
        OutputEndpointLoopbackRecorder recorder)
    {
        public AudioOutputDeviceItem Device { get; } = device;
        public string Path { get; } = path;
        public OutputEndpointLoopbackRecorder Recorder { get; } = recorder;
        public bool HadPayload { get; set; }
    }
}

internal sealed record OutputEndpointCaptureResult(
    string SelectedPath,
    AudioOutputDeviceItem Device,
    double Energy,
    string? Warning,
    IReadOnlyList<string> TemporaryPaths);
