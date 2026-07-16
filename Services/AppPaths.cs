namespace DrumPracticeStudio.Services;

public static class AppPaths
{
    public const string DataRootEnvironmentVariable = "DRUM_PRACTICE_STUDIO_DATA_ROOT";

    public static string Root { get; } = ResolveRoot();

    public static string FactoryContent { get; } = Path.Combine(Root, "FactoryContent", "v2");
    public static string UserLibraries { get; } = Path.Combine(Root, "Libraries");
    public static string DerivedTracks { get; } = Path.Combine(Root, "DerivedTracks");
    public static string StudioStatePath { get; } = Path.Combine(Root, "studio-state.json");
    public static string SeparationRuntime { get; } = Path.Combine(Root, "Runtimes", "Demucs");
    public static string SeparationPython { get; } = Path.Combine(SeparationRuntime, ".venv", "Scripts", "python.exe");
    public static string SeparationModels { get; } = Path.Combine(Root, "Models", "Torch");
    public static string SeparationWork { get; } = Path.Combine(Root, "Work", "Separation");
    public static string RecordingWork { get; } = Path.Combine(Root, "Work", "Recording");
    public static string VstStates { get; } = Path.Combine(Root, "VstStates");
    public static string YouTubeWebViewData { get; } = Path.Combine(Root, "WebView2", "YouTube");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(FactoryContent);
        Directory.CreateDirectory(UserLibraries);
        Directory.CreateDirectory(DerivedTracks);
        Directory.CreateDirectory(SeparationRuntime);
        Directory.CreateDirectory(SeparationModels);
        Directory.CreateDirectory(SeparationWork);
        Directory.CreateDirectory(RecordingWork);
        Directory.CreateDirectory(VstStates);
        Directory.CreateDirectory(YouTubeWebViewData);
    }

    private static string ResolveRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DrumPracticeStudio");
        }

        configuredRoot = configuredRoot.Trim();
        if (!Path.IsPathFullyQualified(configuredRoot))
        {
            throw new InvalidOperationException(
                $"{DataRootEnvironmentVariable} debe contener una ruta absoluta.");
        }

        return Path.GetFullPath(configuredRoot);
    }
}
