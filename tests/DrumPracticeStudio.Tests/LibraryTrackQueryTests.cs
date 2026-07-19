using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class LibraryTrackQueryTests
{
    private static readonly DateTimeOffset OldDate =
        new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset NewDate =
        new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void Apply_FiltersTitlePathAndVariantIgnoringCase()
    {
        var tracks = new[]
        {
            Track("one", "Everlong", @"D:\Rock\everlong.wav", TrackVariant.Original, OldDate),
            Track("two", "Ensayo", @"D:\Jazz\practice.wav", TrackVariant.Recording, NewDate),
            Track("three", "Otro", @"D:\Pop\mix.wav", TrackVariant.GeneratedDrumless, NewDate)
        };

        CollectionAssert.AreEqual(
            new[] { "one" },
            LibraryTrackQuery.Apply(tracks, "EVER", LibraryTrackSortMode.NameAscending)
                .Select(track => track.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "two" },
            LibraryTrackQuery.Apply(tracks, "jazz", LibraryTrackSortMode.NameAscending)
                .Select(track => track.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "three" },
            LibraryTrackQuery.Apply(tracks, "generada", LibraryTrackSortMode.NameAscending)
                .Select(track => track.Id).ToArray());
    }

    [TestMethod]
    public void Apply_OrdersByDateInBothDirections()
    {
        var tracks = new[]
        {
            Track("old", "Zulu", "old.wav", TrackVariant.Original, OldDate),
            Track("new", "Alpha", "new.wav", TrackVariant.Original, NewDate)
        };

        CollectionAssert.AreEqual(
            new[] { "new", "old" },
            LibraryTrackQuery.Apply(tracks, null, LibraryTrackSortMode.DateAddedNewest)
                .Select(track => track.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "old", "new" },
            LibraryTrackQuery.Apply(tracks, null, LibraryTrackSortMode.DateAddedOldest)
                .Select(track => track.Id).ToArray());
    }

    [TestMethod]
    public void Apply_OrdersByNameInBothDirections()
    {
        var tracks = new[]
        {
            Track("z", "Zulu", "z.wav", TrackVariant.Original, OldDate),
            Track("a", "alpha", "a.wav", TrackVariant.Original, NewDate)
        };

        CollectionAssert.AreEqual(
            new[] { "a", "z" },
            LibraryTrackQuery.Apply(tracks, null, LibraryTrackSortMode.NameAscending)
                .Select(track => track.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { "z", "a" },
            LibraryTrackQuery.Apply(tracks, null, LibraryTrackSortMode.NameDescending)
                .Select(track => track.Id).ToArray());
    }

    private static LocalTrack Track(
        string id,
        string title,
        string path,
        TrackVariant variant,
        DateTimeOffset dateAddedUtc) =>
        new()
        {
            Id = id,
            Title = title,
            Path = path,
            Variant = variant,
            DateAddedUtc = dateAddedUtc
        };
}
