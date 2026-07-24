using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DrumPracticeStudio.Models;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace DrumPracticeStudio.Services;

public sealed record AdvancedStemSeparationResult(
    string LeadVocalPath,
    string BackVocalPath,
    string LeadGuitarPath,
    string RhythmGuitarPath);

public sealed class AdvancedStemSeparationService
{
    private const string CacheVersion = "advanced-demucs-v2";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AdvancedStemSeparationService() => AppPaths.EnsureCreated();

    public bool IsInstalled => File.Exists(AppPaths.AdvancedSeparationPython);

    public async Task InstallAsync(
        IProgress<DrumRemovalProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var setupScript = Path.Combine(
            AppContext.BaseDirectory,
            "scripts",
            "setup-advanced-separation.ps1");
        if (!File.Exists(setupScript))
        {
            throw new FileNotFoundException(
                "No se encontró el instalador del motor de separación avanzada.",
                setupScript);
        }

        progress.Report(new DrumRemovalProgress(
            null,
            "Instalando el motor avanzado · Audio Separator, UVR y análisis de guitarra…"));
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
        startInfo.ArgumentList.Add(AppPaths.AdvancedSeparationRuntime);

        var result = await RunProcessAsync(
            startInfo,
            line => progress.Report(new DrumRemovalProgress(null, CleanLine(line))),
            cancellationToken);
        if (result.ExitCode != 0 || !IsInstalled)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(result.LastError)
                    ? "No se pudo instalar el motor de separación avanzada."
                    : $"No se pudo instalar el motor avanzado: {result.LastError}");
        }

        progress.Report(new DrumRemovalProgress(0d, "Motor avanzado preparado"));
    }

    public async Task<AdvancedStemSeparationResult> CreateAsync(
        LocalTrack track,
        string outputDirectory,
        IProgress<DrumRemovalProgress> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        ValidateRequest(track);
        await _gate.WaitAsync(cancellationToken);

        var jobRoot = Path.Combine(AppPaths.SeparationWork, $"advanced-{Guid.NewGuid():N}");
        Directory.CreateDirectory(jobRoot);
        try
        {
            var normalizedInput = Path.Combine(jobRoot, "input.wav");
            progress.Report(new DrumRemovalProgress(0.02d, "Preparando audio para separación avanzada"));
            await NormalizeInputAsync(track.Path, normalizedInput, cancellationToken);

            var cacheRoot = GetCacheRoot(track.Path);
            var cachedVocals = Path.Combine(cacheRoot, "vocals.wav");
            var cachedGuitar = Path.Combine(cacheRoot, "guitar.wav");
            string vocalsPath;
            string guitarPath;

            if (IsUsableStem(cachedVocals) && IsUsableStem(cachedGuitar))
            {
                vocalsPath = cachedVocals;
                guitarPath = cachedGuitar;
                progress.Report(new DrumRemovalProgress(
                    0.34d,
                    "Reutilizando voz y guitarra ya extraídas para esta canción"));
            }
            else
            {
                Directory.CreateDirectory(cacheRoot);
                var demucsRoot = Path.Combine(jobRoot, "demucs");
                var device = await DetectDemucsDeviceAsync(cancellationToken);
                var cpuJobs = ResolveCpuJobs();
                var engineLabel = device == "cuda"
                    ? "GPU NVIDIA"
                    : $"CPU · {cpuJobs} procesos paralelos";
                progress.Report(new DrumRemovalProgress(
                    0.06d,
                    $"Extrayendo voz y guitarra · {engineLabel}"));

                var demucs = BuildDemucsProcess(
                    normalizedInput,
                    demucsRoot,
                    jobRoot,
                    device,
                    cpuJobs);
                var demucsResult = await RunProcessAsync(
                    demucs,
                    line => progress.Report(ParseDemucsProgress(line, engineLabel)),
                    cancellationToken);
                if (demucsResult.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        string.IsNullOrWhiteSpace(demucsResult.LastError)
                            ? $"Demucs terminó con el código {demucsResult.ExitCode}."
                            : $"Demucs falló: {demucsResult.LastError}");
                }

                var generatedVocals = FindStem(demucsRoot, "vocals.wav");
                var generatedGuitar = FindStem(demucsRoot, "guitar.wav");
                vocalsPath = StoreCachedStem(generatedVocals, cachedVocals);
                guitarPath = StoreCachedStem(generatedGuitar, cachedGuitar);
                progress.Report(new DrumRemovalProgress(
                    0.34d,
                    "Voz y guitarra guardadas; no se repetirán si el proceso falla después"));
            }

            var advancedRoot = Path.Combine(jobRoot, "advanced");
            Directory.CreateDirectory(advancedRoot);
            var script = Path.Combine(AppContext.BaseDirectory, "scripts", "advanced-separate.py");
            if (!File.Exists(script))
            {
                throw new FileNotFoundException(
                    "No se encontró el procesador de separación avanzada.",
                    script);
            }

            var advanced = BuildAdvancedProcess(
                script,
                vocalsPath,
                guitarPath,
                advancedRoot,
                jobRoot);
            progress.Report(new DrumRemovalProgress(
                0.35d,
                "Separando voz principal, coros, guitarra solista y guitarra rítmica"));
            var advancedResult = await RunProcessAsync(
                advanced,
                line =>
                {
                    var parsed = ParseAdvancedProgress(line);
                    if (parsed is not null)
                    {
                        progress.Report(parsed);
                    }
                },
                cancellationToken);
            if (advancedResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(advancedResult.LastError)
                        ? $"El motor avanzado terminó con el código {advancedResult.ExitCode}."
                        : $"El motor avanzado falló: {advancedResult.LastError}");
            }

            var resolvedOutputDirectory = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(resolvedOutputDirectory);
            var safeTitle = SanitizeFileName(track.Title);
            var leadVocal = CopyResult(
                FindStem(advancedRoot, "lead-vocal.wav"),
                CreateUniqueDestination(resolvedOutputDirectory, safeTitle, "voz-principal"));
            var backVocal = CopyResult(
                FindStem(advancedRoot, "back-vocal.wav"),
                CreateUniqueDestination(resolvedOutputDirectory, safeTitle, "coros"));
            var leadGuitar = CopyResult(
                FindStem(advancedRoot, "lead-guitar.wav"),
                CreateUniqueDestination(resolvedOutputDirectory, safeTitle, "guitarra-solista-experimental"));
            var rhythmGuitar = CopyResult(
                FindStem(advancedRoot, "rhythm-guitar.wav"),
                CreateUniqueDestination(resolvedOutputDirectory, safeTitle, "guitarra-ritmica-experimental"));

            progress.Report(new DrumRemovalProgress(1d, "Separación avanzada terminada"));
            return new AdvancedStemSeparationResult(
                leadVocal,
                backVocal,
                leadGuitar,
                rhythmGuitar);
        }
        finally
        {
            SafeDeleteJob(jobRoot);
            _gate.Release();
        }
    }

    private static void ValidateRequest(LocalTrack track)
    {
        if (track.Variant != TrackVariant.Original)
        {
            throw new InvalidOperationException(
                "La separación avanzada solo se aplica a pistas originales.");
        }
        if (!File.Exists(track.Path))
        {
            throw new FileNotFoundException("La pista original ya no existe.", track.Path);
        }
        if (!File.Exists(AppPaths.SeparationPython))
        {
            throw new InvalidOperationException(
                "Demucs debe estar instalado antes de ejecutar la separación avanzada.");
        }
    }

    private static ProcessStartInfo BuildDemucsProcess(
        string input,
        string output,
        string workingDirectory,
        string device,
        int cpuJobs)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = AppPaths.SeparationPython,
            WorkingDirectory = workingDirectory,
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
        startInfo.ArgumentList.Add(device);
        startInfo.ArgumentList.Add("--shifts");
        startInfo.ArgumentList.Add("0");
        startInfo.ArgumentList.Add("--overlap");
        startInfo.ArgumentList.Add("0.1");
        startInfo.ArgumentList.Add("--segment");
        startInfo.ArgumentList.Add("7.8");
        if (device == "cpu")
        {
            startInfo.ArgumentList.Add("-j");
            startInfo.ArgumentList.Add(cpuJobs.ToString());
        }
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(output);
        startInfo.ArgumentList.Add("--filename");
        startInfo.ArgumentList.Add("{stem}.{ext}");
        startInfo.ArgumentList.Add(input);
        return startInfo;
    }

    private static ProcessStartInfo BuildAdvancedProcess(
        string script,
        string vocalsPath,
        string guitarPath,
        string advancedRoot,
        string jobRoot)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = AppPaths.AdvancedSeparationPython,
            WorkingDirectory = jobRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.Environment["PYTHONUTF8"] = "1";
        startInfo.Environment["PYTHONUNBUFFERED"] = "1";
        startInfo.ArgumentList.Add(script);
        startInfo.ArgumentList.Add("--vocals");
        startInfo.ArgumentList.Add(vocalsPath);
        startInfo.ArgumentList.Add("--guitar");
        startInfo.ArgumentList.Add(guitarPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(advancedRoot);
        startInfo.ArgumentList.Add("--models");
        startInfo.ArgumentList.Add(AppPaths.AdvancedSeparationModels);
        return startInfo;
    }

    private static async Task<string> DetectDemucsDeviceAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = AppPaths.SeparationPython,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "import torch; print('cuda' if torch.cuda.is_available() else 'cpu')");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return "cpu";
            }
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var output = (await outputTask).Trim();
            return process.ExitCode == 0 && output.Equals("cuda", StringComparison.OrdinalIgnoreCase)
                ? "cuda"
                : "cpu";
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            return "cpu";
        }
    }

    private static int ResolveCpuJobs()
    {
        var processors = Math.Max(1, Environment.ProcessorCount);
        return Math.Clamp(processors / 2, 2, 4);
    }

    private static string GetCacheRoot(string sourcePath)
    {
        var info = new FileInfo(sourcePath);
        var identity = string.Join('|',
            CacheVersion,
            Path.GetFullPath(sourcePath).ToUpperInvariant(),
            info.Length,
            info.LastWriteTimeUtc.Ticks);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        return Path.Combine(AppPaths.SeparationWork, "Cache", "Advanced", hash);
    }

    private static bool IsUsableStem(string path)
    {
        try
        {
            StemAudioMixer.ValidateWave(path);
            return true;
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
            }
            return false;
        }
    }

    private static string StoreCachedStem(string source, string destination)
    {
        StemAudioMixer.ValidateWave(source);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        File.Copy(source, temporary, overwrite: true);
        try
        {
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
            }
        }
        StemAudioMixer.ValidateWave(destination);
        return destination;
    }

    private static async Task NormalizeInputAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) => await Task.Run(() =>
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

    private static DrumRemovalProgress ParseDemucsProgress(string line, string engineLabel)
    {
        var cleaned = CleanLine(line);
        var percentIndex = cleaned.IndexOf('%');
        if (percentIndex > 0)
        {
            var start = percentIndex - 1;
            while (start >= 0 && char.IsDigit(cleaned[start]))
            {
                start--;
            }
            if (double.TryParse(cleaned[(start + 1)..percentIndex], out var percent))
            {
                return new DrumRemovalProgress(
                    0.06d + (Math.Clamp(percent, 0d, 100d) / 100d * 0.28d),
                    $"Extrayendo voz y guitarra · {engineLabel} · {percent:0}%");
            }
        }
        return new DrumRemovalProgress(
            null,
            string.IsNullOrWhiteSpace(cleaned)
                ? $"Extrayendo voz y guitarra · {engineLabel}"
                : cleaned);
    }

    private static DrumRemovalProgress? ParseAdvancedProgress(string line)
    {
        var cleaned = CleanLine(line);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(cleaned);
            var root = document.RootElement;
            if (!root.TryGetProperty("message", out var messageElement))
            {
                return null;
            }
            var message = messageElement.GetString() ?? "Separación avanzada";
            double? percent = null;
            if (root.TryGetProperty("percent", out var percentElement) &&
                percentElement.TryGetDouble(out var value))
            {
                percent = 0.34d + (Math.Clamp(value, 0d, 1d) * 0.64d);
            }
            return new DrumRemovalProgress(percent, message);
        }
        catch (JsonException)
        {
            return new DrumRemovalProgress(null, cleaned);
        }
    }

    private static string FindStem(string root, string fileName) =>
        Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault()
        ?? throw new InvalidDataException($"No se produjo el stem {fileName} esperado.");

    private static string CopyResult(string source, string destination)
    {
        StemAudioMixer.ValidateWave(source);
        File.Copy(source, destination, overwrite: false);
        StemAudioMixer.ValidateWave(destination);
        return destination;
    }

    private static string CreateUniqueDestination(string directory, string title, string suffix)
    {
        var baseName = $"{title} · {suffix}";
        var path = Path.Combine(directory, $"{baseName}.wav");
        var index = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{baseName} ({index++}).wav");
        }
        return path;
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safe) ? "pista" : safe.Trim();
    }

    private static string CleanLine(string value) => value
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Trim();

    private static async Task<ProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        Action<string> onOutput,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var errors = new Queue<string>();
        process.OutputDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                onOutput(args.Data);
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }
            if (errors.Count >= 12)
            {
                errors.Dequeue();
            }
            errors.Enqueue(args.Data);
            onOutput(args.Data);
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"No se pudo iniciar {startInfo.FileName}.");
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
            }
        });

        await process.WaitForExitAsync(cancellationToken);
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, string.Join(Environment.NewLine, errors));
    }

    private static void SafeDeleteJob(string jobRoot)
    {
        try
        {
            if (Directory.Exists(jobRoot))
            {
                Directory.Delete(jobRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed record ProcessResult(int ExitCode, string LastError);
}
