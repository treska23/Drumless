using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DrumPracticeStudio.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DrumPracticeStudio.Services;

public sealed record DrumRemovalProgress(double? Percent, string Message);

public sealed record DrumRemovalResult(string DrumlessPath);

public sealed partial class DrumRemovalService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DrumRemovalService() => AppPaths.EnsureCreated();

    public bool IsInstalled => File.Exists(AppPaths.SeparationPython);

    public async Task InstallAsync(
        IProgress<DrumRemovalProgress> progress,
        CancellationToken cancellationToken)
    {
        var setupScript = Path.Combine(AppContext.BaseDirectory, "scripts", "setup-demucs.ps1");
        if (!File.Exists(setupScript))
        {
            throw new FileNotFoundException("No se encontró el instalador local de Demucs.", setupScript);
        }

        progress.Report(new DrumRemovalProgress(null, "Descargando el motor local de separación…"));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(setupScript);
        startInfo.ArgumentList.Add("-InstallRoot");
        startInfo.ArgumentList.Add(AppPaths.SeparationRuntime);

        var exitCode = await RunProcessAsync(
            startInfo,
            line => progress.Report(new DrumRemovalProgress(null, CleanSetupLine(line))),
            cancellationToken);

        if (exitCode != 0 || !IsInstalled)
        {
            throw new InvalidOperationException("No se pudo instalar el motor de separación local.");
        }

        progress.Report(new DrumRemovalProgress(0d, "Motor local preparado"));
    }

    public async Task<DrumRemovalResult> CreateStemMixAsync(
        LocalTrack track,
        string outputDirectory,
        StemSelection selection,
        IProgress<DrumRemovalProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        StemMixPlan.Validate(selection);

        if (track.Variant != TrackVariant.Original)
        {
            throw new InvalidOperationException("Solo se separan pistas locales originales.");
        }

        if (!File.Exists(track.Path))
        {
            throw new FileNotFoundException("La pista original ya no existe.", track.Path);
        }

        if (!IsInstalled)
        {
            throw new InvalidOperationException("El motor Demucs todavía no está instalado.");
        }

        var resolvedOutputDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(resolvedOutputDirectory);

        await _gate.WaitAsync(cancellationToken);
        var jobRoot = Path.Combine(AppPaths.SeparationWork, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobRoot);

        try
        {
            progress.Report(new DrumRemovalProgress(0.03d, "Validando la pista original"));
            var normalizedInput = Path.Combine(jobRoot, "input.wav");
            await NormalizeInputAsync(track.Path, normalizedInput, cancellationToken);
            progress.Report(new DrumRemovalProgress(0.18d, "Audio preparado · iniciando Demucs"));

            var startInfo = new ProcessStartInfo
            {
                FileName = AppPaths.SeparationPython,
                WorkingDirectory = jobRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.Environment["TORCH_HOME"] = AppPaths.SeparationModels;
            startInfo.Environment["PYTHONUTF8"] = "1";
            startInfo.Environment["PYTHONUNBUFFERED"] = "1";
            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add("demucs.separate");
            startInfo.ArgumentList.Add("-n");
            startInfo.ArgumentList.Add("htdemucs_6s");
            startInfo.ArgumentList.Add("-d");
            startInfo.ArgumentList.Add("cpu");
            startInfo.ArgumentList.Add("--shifts");
            startInfo.ArgumentList.Add("0");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(jobRoot);
            startInfo.ArgumentList.Add("--filename");
            startInfo.ArgumentList.Add("{stem}.{ext}");
            startInfo.ArgumentList.Add(normalizedInput);

            var exitCode = await RunProcessAsync(
                startInfo,
                line => progress.Report(ParseDemucsProgress(line)),
                cancellationToken);
            if (exitCode != 0)
            {
                throw new InvalidOperationException($"Demucs terminó con el código {exitCode}.");
            }

            progress.Report(new DrumRemovalProgress(0.92d, "Validando el resultado"));
            var safeTitle = SanitizeFileName(track.Title);
            var destination = CreateUniqueDestination(
                resolvedOutputDirectory,
                safeTitle,
                StemMixPlan.FileSuffix(selection));
            await StemAudioMixer.MixAsync(jobRoot, selection, destination, cancellationToken);
            WriteMetadata(track, destination, selection);

            progress.Report(new DrumRemovalProgress(
                1d,
                $"Mezcla creada · {StemMixPlan.Describe(selection)}"));
            return new DrumRemovalResult(destination);
        }
        finally
        {
            SafeDeleteJob(jobRoot);
            _gate.Release();
        }
    }

    public Task<DrumRemovalResult> CreateDrumlessAsync(
        LocalTrack track,
        string outputDirectory,
        IProgress<DrumRemovalProgress> progress,
        CancellationToken cancellationToken) =>
        CreateStemMixAsync(
            track,
            outputDirectory,
            StemSelection.Drumless,
            progress,
            cancellationToken);

    public Task<DrumRemovalResult> CreateDrumlessAsync(
        LocalTrack track,
        IProgress<DrumRemovalProgress> progress,
        CancellationToken cancellationToken) =>
        CreateStemMixAsync(
            track,
            AppPaths.DerivedTracks,
            StemSelection.Drumless,
            progress,
            cancellationToken);

    private static Task NormalizeInputAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reader = new AudioFileReader(sourcePath);
        ISampleProvider provider = reader;
        provider = provider.WaveFormat.Channels switch
        {
            1 => new MonoToStereoSampleProvider(provider),
            2 => provider,
            _ => throw new NotSupportedException("La pista debe ser mono o estéreo.")
        };
        if (provider.WaveFormat.SampleRate != 44_100)
        {
            provider = new WdlResamplingSampleProvider(provider, 44_100);
        }

        WaveFileWriter.CreateWaveFile16(destinationPath, provider);
        cancellationToken.ThrowIfCancellationRequested();
    }, cancellationToken);

    private static async Task<int> RunProcessAsync(
        ProcessStartInfo startInfo,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                onOutput(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                onOutput(args.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"No se pudo iniciar {startInfo.FileName}.");
        }

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
            // La prioridad es una optimización; no bloquea el trabajo.
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var registration = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // El proceso puede haber terminado entre la comprobación y Kill.
            }
        });

        await process.WaitForExitAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static DrumRemovalProgress ParseDemucsProgress(string line)
    {
        var match = PercentRegex().Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
        {
            var mapped = 0.2d + Math.Clamp(percent, 0, 100) / 100d * 0.7d;
            return new DrumRemovalProgress(mapped, $"Separando stems · {percent}%");
        }

        return new DrumRemovalProgress(null, line.Trim());
    }

    private static string CleanSetupLine(string line)
    {
        var cleaned = line.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "Preparando motor local…" : cleaned;
    }

    private static string CreateUniqueDestination(
        string outputDirectory,
        string safeTitle,
        string mixSuffix)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var candidate = Path.Combine(outputDirectory, $"{safeTitle} - {mixSuffix} - {stamp}.wav");
        var suffix = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(
                outputDirectory,
                $"{safeTitle} - {mixSuffix} - {stamp}-{suffix++}.wav");
        }

        return candidate;
    }

    private static void WriteMetadata(
        LocalTrack source,
        string outputPath,
        StemSelection selection)
    {
        var metadataPath = Path.ChangeExtension(outputPath, ".separation.json");
        var metadata = new
        {
            schemaVersion = 1,
            source.Id,
            source.Title,
            source.Path,
            outputPath,
            keptStems = StemMixPlan.GetFileNames(selection)
                .Select(Path.GetFileNameWithoutExtension)
                .ToArray(),
            engine = "demucs",
            engineVersion = "4.0.1",
            model = "htdemucs_6s",
            createdAtUtc = DateTimeOffset.UtcNow
        };
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(metadata, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(safe) ? "pista" : safe;
    }

    private static void SafeDeleteJob(string jobRoot)
    {
        try
        {
            var resolvedRoot = Path.GetFullPath(AppPaths.SeparationWork)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var resolvedJob = Path.GetFullPath(jobRoot)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (resolvedJob.StartsWith(resolvedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(jobRoot))
            {
                Directory.Delete(jobRoot, recursive: true);
            }
        }
        catch
        {
            // Una limpieza pendiente no debe ocultar el resultado o error principal.
        }
    }

    [GeneratedRegex(@"(\d{1,3})%")]
    private static partial Regex PercentRegex();
}
