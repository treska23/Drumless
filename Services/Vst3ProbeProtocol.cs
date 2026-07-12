using System.Text.Json;
using NAudio.Vst3;

namespace DrumPracticeStudio.Services;

internal sealed record Vst3ProbedClass(
    string ClassId,
    string Category,
    string Name,
    string Vendor,
    string Version,
    string SdkVersion,
    string SubCategories);

internal static class Vst3ProbeProtocol
{
    public const string Argument = "--vst3-probe";

    public static int Execute(string modulePath, string outputPath)
    {
        try
        {
            using var module = Vst3Module.Load(modulePath);
            var classes = module.GetClasses()
                .Where(candidate => candidate.IsInstrument)
                .Select(candidate => new Vst3ProbedClass(
                    candidate.ClassId,
                    candidate.Category,
                    candidate.Name,
                    candidate.Vendor,
                    candidate.Version,
                    candidate.SdkVersion,
                    candidate.SubCategories))
                .ToArray();
            File.WriteAllText(outputPath, JsonSerializer.Serialize(classes));
            return 0;
        }
        catch
        {
            return 1;
        }
    }
}
