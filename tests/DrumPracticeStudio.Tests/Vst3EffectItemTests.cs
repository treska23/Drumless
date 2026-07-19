using DrumPracticeStudio.Models;
using NAudio.Vst3;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class Vst3EffectItemTests
{
    [TestMethod]
    public void EffectType_UsesSpecificVstSubCategory()
    {
        var item = Create("Vintage Compressor", "Steinberg", "Fx|Dynamics|Compressor");

        Assert.AreEqual("Compressor", item.EffectType);
    }

    [TestMethod]
    [DataRow("vintage")]
    [DataRow("stein")]
    [DataRow("compressor")]
    [DataRow("dynamics vintage")]
    public void MatchesSearch_FindsNameVendorCategoryAndMultipleTerms(string query)
    {
        var item = Create("Vintage Compressor", "Steinberg", "Fx|Dynamics|Compressor");

        Assert.IsTrue(item.MatchesSearch(query));
    }

    [TestMethod]
    public void MatchesSearch_RejectsUnrelatedText()
    {
        var item = Create("Vintage Compressor", "Steinberg", "Fx|Dynamics|Compressor");

        Assert.IsFalse(item.MatchesSearch("reverb"));
    }

    private static Vst3EffectItem Create(string name, string vendor, string subCategories) =>
        new(
            new Vst3ModuleInfo(@"C:\VST3\Test.vst3", "Test"),
            new Vst3ClassInfo(
                Guid.NewGuid().ToString("N"),
                Vst3ClassInfo.AudioModuleCategory,
                name,
                vendor,
                "1.0",
                "VST 3.7",
                subCategories));
}
