using System.Security.Cryptography;
using System.Text;
using DrumPracticeStudio.Models;

namespace DrumPracticeStudio.Services;

internal static class Vst3EffectStateFiles
{
    public static string GetAutomaticPath(
        string slotId,
        Vst3EffectReference reference,
        string? stateDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        var identity = Encoding.UTF8.GetBytes(
            $"{reference.ModulePath}|{reference.ClassId}|{reference.PresetPath}|" +
            string.Join(",", reference.EffectiveParameterSettings.Select(setting =>
                $"{setting.Id}:{setting.NormalizedValue:R}")));
        var fingerprint = Convert.ToHexString(SHA256.HashData(identity))[..16];
        var safeSlotId = string.Concat((slotId ?? string.Empty).Where(char.IsLetterOrDigit));
        if (string.IsNullOrWhiteSpace(safeSlotId))
        {
            safeSlotId = "slot";
        }
        else if (safeSlotId.Length > 64)
        {
            safeSlotId = safeSlotId[..64];
        }

        return Path.Combine(
            stateDirectory ?? AppPaths.VstStates,
            $"effect-{safeSlotId}-{fingerprint}.vstpreset");
    }
}
