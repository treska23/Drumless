using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class TrackLibraryServiceTests
{
    [TestMethod]
    public void ScanFolder_AddsSupportedFilesRecursivelyAndDeduplicates()
    {
        using var temporary = new TemporaryDirectory();
        var outputFolder = temporary.Combine("generated");
        var nestedFolder = System.IO.Path.Combine(outputFolder, "album");
        var workFolder = System.IO.Path.Combine(outputFolder, ".work");
        Directory.CreateDirectory(nestedFolder);
        Directory.CreateDirectory(workFolder);
        var firstPath = System.IO.Path.Combine(outputFolder, "first.wav");
        var secondPath = System.IO.Path.Combine(nestedFolder, "second.MP3");
        File.WriteAllBytes(firstPath, [1]);
        File.WriteAllBytes(secondPath, [2]);
        File.WriteAllText(System.IO.Path.Combine(outputFolder, "notes.txt"), "ignorar");
        File.WriteAllBytes(System.IO.Path.Combine(workFolder, "temporary.wav"), [3]);
        var library = new TrackLibraryService();

        var firstScan = library.ScanFolder(outputFolder);
        var secondScan = library.ScanFolder(outputFolder);
        var duplicate = library.RegisterGenerated(firstPath.ToUpperInvariant());

        Assert.AreEqual(2, firstScan.Count);
        Assert.AreEqual(0, secondScan.Count);
        Assert.AreEqual(2, library.Tracks.Count);
        Assert.IsTrue(library.Tracks.All(track => track.Variant == TrackVariant.GeneratedDrumless));
        Assert.IsTrue(library.Tracks.All(track => !track.IsMissing));
        Assert.AreSame(
            library.Tracks.Single(track =>
                string.Equals(track.Path, firstPath, StringComparison.OrdinalIgnoreCase)),
            duplicate);
    }

    [TestMethod]
    public void LoadAndRefreshAvailability_RetainsMissingExternalTracks()
    {
        using var temporary = new TemporaryDirectory();
        var existingPath = temporary.Combine("external.wav");
        var missingPath = temporary.Combine("missing.flac");
        File.WriteAllBytes(existingPath, [1]);
        var library = new TrackLibraryService(
        [
            new TrackRecord
            {
                Id = "existing",
                Title = "External",
                Path = existingPath,
                Variant = TrackVariant.UserDrumless
            },
            new TrackRecord
            {
                Id = "missing",
                Title = "Missing",
                Path = missingPath,
                Variant = TrackVariant.Original
            }
        ]);

        Assert.IsFalse(library.Tracks.Single(track => track.Id == "existing").IsMissing);
        Assert.IsTrue(library.Tracks.Single(track => track.Id == "missing").IsMissing);

        File.Delete(existingPath);
        library.RefreshAvailability();

        Assert.AreEqual(2, library.Tracks.Count);
        Assert.IsTrue(library.Tracks.All(track => track.IsMissing));
        CollectionAssert.AreEquivalent(
            new[] { "existing", "missing" },
            library.Snapshot().Select(track => track.Id).ToArray());
    }

    [TestMethod]
    public void ScanFolder_WhenFolderChanges_KeepsPreviousTracksAndFiles()
    {
        using var temporary = new TemporaryDirectory();
        var oldFolder = temporary.Combine("old-output");
        var newFolder = temporary.Combine("new-output");
        Directory.CreateDirectory(oldFolder);
        Directory.CreateDirectory(newFolder);
        var oldPath = System.IO.Path.Combine(oldFolder, "old.wav");
        var newPath = System.IO.Path.Combine(newFolder, "new.flac");
        File.WriteAllBytes(oldPath, [1]);
        File.WriteAllBytes(newPath, [2]);
        var library = new TrackLibraryService();

        library.ScanFolder(oldFolder);
        library.ScanFolder(newFolder);

        Assert.AreEqual(2, library.Tracks.Count);
        Assert.IsTrue(library.Tracks.Any(track => track.Path == Path.GetFullPath(oldPath)));
        Assert.IsTrue(library.Tracks.Any(track => track.Path == Path.GetFullPath(newPath)));
        Assert.IsTrue(File.Exists(oldPath), "Cambiar la carpeta no debe mover ni borrar el audio anterior.");
        Assert.IsTrue(File.Exists(newPath));
    }
}
