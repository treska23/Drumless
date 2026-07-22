using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class SongIdentityResolverTests
{
    [TestMethod]
    public void TryResolve_RecognizesArtistAndSongFromFileName()
    {
        var resolved = SongIdentityResolver.TryResolve(
            "Foo Fighters - Everlong (drumless).wav",
            out var artist,
            out var song);

        Assert.IsTrue(resolved);
        Assert.AreEqual("Foo Fighters", artist);
        Assert.AreEqual("Everlong", song);
    }

    [TestMethod]
    public void TryResolve_RequestsHelpWhenArtistIsMissing()
    {
        var resolved = SongIdentityResolver.TryResolve(
            "Everlong.wav",
            out var artist,
            out var song);

        Assert.IsFalse(resolved);
        Assert.AreEqual(string.Empty, artist);
        Assert.AreEqual("Everlong", song);
    }

    [TestMethod]
    public void TryResolve_TrackNumberWithDotPreservesSuggestedSongTitle()
    {
        var resolved = SongIdentityResolver.TryResolve(
            "04.-Other Side",
            out var artist,
            out var song);

        Assert.IsFalse(resolved);
        Assert.AreEqual(string.Empty, artist);
        Assert.AreEqual("Other Side", song);
    }
}
