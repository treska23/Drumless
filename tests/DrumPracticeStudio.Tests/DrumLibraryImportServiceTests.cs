using System.IO.Compression;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class DrumLibraryImportServiceTests
{
    [TestMethod]
    public void ImportFolder_BuildsVelocityLayersAndRoundRobinGroups()
    {
        using var temporary = new TemporaryDirectory();
        foreach (var name in new[]
                 {
                     "kick_soft_rr1.wav", "kick_soft_rr2.wav", "kick_hard_rr1.wav",
                     "snare_vel40_rr1.wav", "snare_vel110_rr1.wav", "hihat_closed_medium.wav",
                     "hihat_open_hard.wav", "tom_high.wav", "tom_low.wav", "crash.wav", "ride.wav"
                 })
        {
            File.WriteAllBytes(temporary.Combine(name), []);
        }

        var result = new DrumLibraryImportService().ImportFolder(temporary.Path);

        Assert.AreEqual(11, result.ImportedFiles);
        Assert.AreEqual(0, result.SkippedFiles);
        Assert.AreEqual(8, result.Kit.Pads.Count);

        var kick = result.Kit.Pads.Single(pad => pad.Articulation == "kick.main");
        Assert.AreEqual(2, kick.Layers.Count);
        Assert.AreEqual(2, kick.Layers[0].Samples.Count);
        Assert.AreEqual(1, kick.Layers[0].MinVelocity);
        Assert.AreEqual(72, kick.Layers[0].MaxVelocity);
        Assert.AreEqual(73, kick.Layers[1].MinVelocity);
        Assert.AreEqual(127, kick.Layers[1].MaxVelocity);
    }

    [TestMethod]
    public void ImportFolder_UsesOneFullRangeLayerWhenFilesHaveNoVelocityName()
    {
        using var temporary = new TemporaryDirectory();
        File.WriteAllBytes(temporary.Combine("bombo_rr1.wav"), []);
        File.WriteAllBytes(temporary.Combine("bombo_rr2.wav"), []);
        File.WriteAllBytes(temporary.Combine("notes.wav"), []);

        var result = new DrumLibraryImportService().ImportFolder(temporary.Path);
        var layer = result.Kit.Pads.Single().Layers.Single();

        Assert.AreEqual(1, layer.MinVelocity);
        Assert.AreEqual(127, layer.MaxVelocity);
        Assert.AreEqual(2, layer.Samples.Count);
        Assert.AreEqual(1, result.SkippedFiles);
    }

    [TestMethod]
    public void ImportZip_ExtractsImportsAndRemovesTemporaryFiles()
    {
        using var temporary = new TemporaryDirectory();
        var zipPath = temporary.Combine("Salamander.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("Samples/kick_P_1.wav");
            archive.CreateEntry("Samples/kick_F_1.wav");
            archive.CreateEntry("Samples/kick_FF_1.wav");
            archive.CreateEntry("Samples/hitom_P_1.wav");
            archive.CreateEntry("Samples/lotom_FF_1.wav");
        }

        var copiedFolder = temporary.Combine("Copied");
        Directory.CreateDirectory(copiedFolder);
        var result = new DrumLibraryImportService().ImportZip(zipPath, source =>
        {
            var destination = Path.Combine(copiedFolder, $"{Guid.NewGuid():N}.wav");
            File.Copy(source, destination);
            return destination;
        });

        Assert.AreEqual("Salamander", result.Kit.Name);
        Assert.AreEqual(5, result.ImportedFiles);
        Assert.AreEqual(3, result.Kit.Pads.Single(pad => pad.Articulation == "kick.main").Layers.Count);
        Assert.IsTrue(result.Kit.Pads.Any(pad => pad.Articulation == "tom.high"));
        Assert.IsTrue(result.Kit.Pads.Any(pad => pad.Articulation == "tom.low"));
        Assert.AreEqual(5, Directory.EnumerateFiles(copiedFolder).Count());
    }
}
