using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class YouTubeNavigationServiceTests
{
    [TestMethod]
    public void CreateSearchUri_UsesOfficialYouTubeSearchAndEscapesTheQuery()
    {
        var uri = YouTubeNavigationService.CreateSearchUri("  rock & funk drumless  ");

        Assert.AreEqual("https", uri.Scheme);
        Assert.AreEqual("www.youtube.com", uri.Host);
        Assert.AreEqual("/results", uri.AbsolutePath);
        Assert.AreEqual("?search_query=rock%20%26%20funk%20drumless", uri.Query);
        Assert.IsTrue(YouTubeNavigationService.IsYouTubeUri(uri));
    }

    [TestMethod]
    public void IsYouTubeUri_RejectsLookalikeDomains()
    {
        Assert.IsTrue(YouTubeNavigationService.IsYouTubeUri(new Uri("https://youtu.be/abc")));
        Assert.IsTrue(YouTubeNavigationService.IsYouTubeUri(new Uri("https://m.youtube.com/watch?v=abc")));
        Assert.IsFalse(YouTubeNavigationService.IsYouTubeUri(new Uri("https://youtube.com.example.test/")));
        Assert.IsFalse(YouTubeNavigationService.IsYouTubeUri(new Uri("file:///C:/video.html")));
    }

    [TestMethod]
    public void TryGetVideoId_SupportsWatchShortAndShortsUrls()
    {
        Assert.IsTrue(YouTubeNavigationService.TryGetVideoId(
            new Uri("https://www.youtube.com/watch?v=abc_DEF-123"), out var watchId));
        Assert.AreEqual("abc_DEF-123", watchId);
        Assert.IsTrue(YouTubeNavigationService.TryGetVideoId(
            new Uri("https://youtu.be/abc_DEF-123"), out var shortId));
        Assert.AreEqual("abc_DEF-123", shortId);
        Assert.IsTrue(YouTubeNavigationService.TryGetVideoId(
            new Uri("https://www.youtube.com/shorts/abc_DEF-123"), out var shortsId));
        Assert.AreEqual("abc_DEF-123", shortsId);
        Assert.IsFalse(YouTubeNavigationService.TryGetVideoId(
            new Uri("https://www.youtube.com/results?search_query=drumless"), out _));
    }
}
