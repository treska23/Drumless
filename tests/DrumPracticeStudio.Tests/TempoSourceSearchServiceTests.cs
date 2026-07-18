using System.Net;
using System.Net.Http;
using System.Text;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class TempoSourceSearchServiceTests
{
    [TestMethod]
    public async Task SearchAsync_ReturnsOnlyExplicitBpmWithSourceEvidence()
    {
        const string html = """
            <div class="result results_links">
              <a class="result__a" href="https://example.test/song">Artist - Song tempo</a>
              <a class="result__snippet">The verified tempo is 128 BPM in common time.</a>
            </div>
            <div class="result results_links">
              <a class="result__a" href="https://example.test/no-number">Artist biography</a>
              <a class="result__snippet">No tempo appears in this result.</a>
            </div>
            """;
        using var internet = new HttpClient(new StaticHandler(html));
        using var ollama = new HttpClient(new StaticHandler("{}"))
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/")
        };
        using var service = new TempoSourceSearchService(internet, ollama);

        var results = await service.SearchAsync("Artist - Song (drumless).wav");

        Assert.AreEqual(1, results.Count);
        Assert.AreEqual(128d, results[0].Bpm);
        Assert.AreEqual("https://example.test/song", results[0].SourceUrl);
        StringAssert.Contains(results[0].Evidence, "128 BPM");
    }

    [TestMethod]
    public void NormalizeTitle_RemovesFileAndBackingTrackNoise()
    {
        Assert.AreEqual(
            "Artist Song",
            TempoSourceSearchService.NormalizeTitle(
                "Artist - Song (official audio) drumless.wav"));
    }

    private sealed class StaticHandler(string response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "text/html")
        });
    }
}
