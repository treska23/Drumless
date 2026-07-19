using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class TrackLibraryServiceTests
{
    [TestMethod]
    public void RegisterRecording_PersistsAsAPlayableLibraryTake()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("take.wav");
        File.WriteAllBytes(path, [1, 2, 3, 4]);
        var library = new TrackLibraryService();

        var registered = library.RegisterRecording(path, "Toma uno");
        var restored = new TrackLibraryService(library.Snapshot());

        Assert.AreEqual(TrackVariant.Recording, registered.Variant);
        Assert.IsTrue(restored.TryGetById(registered.Id, out var loaded));
        Assert.AreEqual("Toma uno", loaded.Title);
        Assert.AreEqual(TrackVariant.Recording, loaded.Variant);
        Assert.IsTrue(loaded.IsAvailable);
        Assert.AreEqual(registered.DateAddedUtc, loaded.DateAddedUtc);
    }

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

    [TestMethod]
    public void RegisterGenerated_AddsDemucsResultImmediatelyAndPersistsIt()
    {
        using var temporary = new TemporaryDirectory();
        var drumlessPath = temporary.Combine("song-no-drums.wav");
        File.WriteAllBytes(drumlessPath, [1, 2, 3]);
        var library = new TrackLibraryService();

        var generated = library.RegisterGenerated(drumlessPath, "Song · sin batería");

        Assert.AreSame(generated, library.Tracks.Single());
        Assert.AreEqual(TrackVariant.GeneratedDrumless, generated.Variant);
        Assert.IsTrue(generated.IsAvailable);
        var restored = new TrackLibraryService(library.Snapshot());
        Assert.AreEqual(1, restored.Tracks.Count);
        Assert.AreEqual(generated.Id, restored.Tracks[0].Id);
        Assert.IsTrue(restored.Tracks[0].IsAvailable);
    }

    [TestMethod]
    public void Remove_DeletesOnlyTheLibraryRecordAndKeepsTheAudioFile()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("keep-me.wav");
        File.WriteAllBytes(path, [1, 2, 3]);
        var library = new TrackLibraryService();
        var track = library.RegisterImported(path, TrackVariant.Original);

        var removed = library.Remove(track.Id);

        Assert.IsTrue(removed);
        Assert.AreEqual(0, library.Tracks.Count);
        Assert.IsFalse(library.TryGetById(track.Id, out _));
        Assert.IsTrue(File.Exists(path));
    }
}
