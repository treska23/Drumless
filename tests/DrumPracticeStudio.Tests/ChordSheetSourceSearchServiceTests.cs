using System.Net;
using System.Net.Http;
using System.Text;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class ChordSheetSourceSearchServiceTests
{
    [TestMethod]
    public async Task SearchAsync_ReturnsChordAndLyricsCandidatesWithOriginalLinks()
    {
        const string html = """
            <div class="result results_links">
              <a class="result__a" href="https://tabs.example.test/artist/song">Artist Song chords</a>
              <a class="result__snippet">Lyrics, chords and guitar tabs for the complete song.</a>
            </div>
            <div class="result results_links">
              <a class="result__a" href="https://news.example.test/artist">Artist interview</a>
              <a class="result__snippet">An interview without musical notation.</a>
            </div>
            """;
        using var http = new HttpClient(new StaticHandler(html));
        using var service = new ChordSheetSourceSearchService(http);

        var results = await service.SearchAsync("Artist - Song (drumless).wav");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(
            "https://tabs.example.test/artist/song",
            results[0].SourceUrl);
        Assert.AreEqual("tabs.example.test", results[0].SourceName);
        StringAssert.Contains(results[0].Evidence, "Lyrics");
    }

    private sealed class StaticHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "text/html")
            });
    }
}
