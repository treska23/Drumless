using System.Net;
using System.Net.Http;
using System.Text;
using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class SongEffectRecommendationServiceTests
{
    [TestMethod]
    public async Task RecommendAsync_UsesOnlyInstalledEffectsAndFixedMonoInputs()
    {
        const string internetHtml = """
            <a class="result__a">Artist Song production</a>
            <a class="result__snippet">Dry vocal and driven mono guitar.</a>
            """;
        const string ollamaResult = """
            {
              "message": {
                "content": "{\"summary\":\"Aproximación prudente\",\"guitar\":{\"description\":\"Guitarra con drive\",\"slots\":[{\"catalogId\":\"guitar\",\"purpose\":\"Amplificador\",\"presetHint\":\"British crunch\",\"mix\":0.8},{\"catalogId\":\"guitar\",\"purpose\":\"Duplicado\",\"presetHint\":\"\",\"mix\":0.4},{\"catalogId\":\"inventado\",\"purpose\":\"No válido\",\"presetHint\":\"\",\"mix\":1}]},\"voice\":{\"description\":\"Voz controlada\",\"slots\":[{\"catalogId\":\"voice\",\"purpose\":\"Compresión\",\"presetHint\":\"Vocal gentle\",\"mix\":0.65}]}}"
              }
            }
            """;
        using var internet = new HttpClient(new StaticHandler(_ => internetHtml));
        using var ollama = new HttpClient(new StaticHandler(request =>
            request.RequestUri?.AbsolutePath.EndsWith("/api/tags", StringComparison.Ordinal) == true
                ? "{\"models\":[{\"name\":\"llama-test\"}]}"
                : ollamaResult))
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/")
        };
        using var service = new SongEffectRecommendationService(internet, ollama);
        var guitar = CreateEffect("guitar", "Guitar Rig", "Amp Simulator");
        var voice = CreateEffect("voice", "Vocal Rider", "Dynamics");

        var profile = await service.RecommendAsync(new SongEffectRecommendationRequest(
            "local:track-1",
            "Artist - Song.wav",
            "Artist",
            "Song",
            120d,
            ["Intro", "Estribillo"],
            [guitar, voice]));

        Assert.AreEqual("llama-test", profile.OllamaModel);
        Assert.AreEqual("Artist", profile.Artist);
        Assert.AreEqual("Song", profile.SongTitle);
        Assert.AreEqual(0, profile.Guitar.ChannelIndex);
        Assert.AreEqual(1, profile.Voice.ChannelIndex);
        Assert.AreEqual(1, profile.Guitar.Slots.Count);
        Assert.AreEqual("Guitar Rig", profile.Guitar.Slots[0].Effect.Name);
        Assert.AreEqual(0.8d, profile.Guitar.Slots[0].Mix);
        Assert.AreEqual(1, profile.Voice.Slots.Count);
        Assert.AreEqual("Vocal Rider", profile.Voice.Slots[0].Effect.Name);
    }

    private static InstalledEffectDescriptor CreateEffect(
        string id,
        string name,
        string effectType) => new(
        id,
        effectType,
        new Vst3EffectReference(
            $@"C:\VST3\{name}.vst3",
            name,
            id.PadRight(32, '0'),
            "Audio Module Class",
            name,
            "Test Vendor",
            "1.0",
            "VST 3.7",
            $"Fx|{effectType}"));

    private sealed class StaticHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(
            HttpStatusCode.OK)
        {
            Content = new StringContent(response(request), Encoding.UTF8, "application/json")
        });
    }
}
