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

    [TestMethod]
    public async Task RecommendAsync_AcceptsPluginNamesAndAlternativeFieldsFromOllama()
    {
        const string ollamaResult = """
            {
              "message": {
                "content": "{\"summary\":\"Por nombre\",\"guitar\":{\"slots\":[{\"catalogId\":\"Guitar Rig\",\"purpose\":\"Amplificador\"}]},\"voice\":{\"slots\":[{\"pluginName\":\"Vocal Rider · Waves\",\"purpose\":\"Dinámica\"}]}}"
              }
            }
            """;
        using var internet = new HttpClient(new StaticHandler(_ => string.Empty));
        using var ollama = new HttpClient(new StaticHandler(request =>
            request.RequestUri?.AbsolutePath.EndsWith("/api/tags", StringComparison.Ordinal) == true
                ? "{\"models\":[{\"name\":\"qwen-test\"}]}"
                : ollamaResult))
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/")
        };
        using var service = new SongEffectRecommendationService(internet, ollama);

        var profile = await service.RecommendAsync(new SongEffectRecommendationRequest(
            "local:track-2",
            "Artist - Song",
            "Artist",
            "Song",
            null,
            [],
            [
                CreateEffect("fx-031", "Guitar Rig", "Amp Simulator"),
                CreateEffect("fx-205", "Vocal Rider", "Dynamics")
            ]));

        Assert.AreEqual("Guitar Rig", profile.Guitar.Slots.Single().Effect.Name);
        Assert.AreEqual("Vocal Rider", profile.Voice.Slots.Single().Effect.Name);
        Assert.IsFalse(profile.Summary.Contains(
            "huecos se completaron",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task RecommendAsync_CompletesInvalidOllamaSelectionWithInstalledEffects()
    {
        const string ollamaResult = """
            {
              "message": {
                "content": "{\"summary\":\"Selección incompleta\",\"guitar\":{\"slots\":[{\"catalogId\":\"plugin inventado\"}]},\"voice\":{\"slots\":[]}}"
              }
            }
            """;
        using var internet = new HttpClient(new StaticHandler(_ => string.Empty));
        using var ollama = new HttpClient(new StaticHandler(request =>
            request.RequestUri?.AbsolutePath.EndsWith("/api/tags", StringComparison.Ordinal) == true
                ? "{\"models\":[{\"name\":\"qwen-test\"}]}"
                : ollamaResult))
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/")
        };
        using var service = new SongEffectRecommendationService(internet, ollama);
        var installed = new[]
        {
            CreateEffect("fx-031", "Guitar Rig Mono", "Amp Simulator"),
            CreateEffect("fx-205", "Vocal Rider Mono", "Dynamics"),
            CreateEffect("fx-222", "Studio Reverb Mono", "Reverb")
        };

        var profile = await service.RecommendAsync(new SongEffectRecommendationRequest(
            "local:track-3",
            "Artist - Song",
            "Artist",
            "Song",
            null,
            [],
            installed));

        Assert.IsTrue(profile.Guitar.Slots.Count > 0);
        Assert.IsTrue(profile.Voice.Slots.Count > 0);
        Assert.IsTrue(profile.Guitar.Slots.All(slot =>
            installed.Any(effect => effect.Reference.Name == slot.Effect.Name)));
        Assert.IsTrue(profile.Voice.Slots.All(slot =>
            installed.Any(effect => effect.Reference.Name == slot.Effect.Name)));
        StringAssert.Contains(profile.Summary, "huecos se completaron");
    }

    [TestMethod]
    public async Task TuneParametersAsync_AppliesOnlyEnumeratedParameterIds()
    {
        const string tuningResult = """
            {
              "message": {
                "content": "{\"guitar\":[{\"slotId\":\"g0\",\"parameters\":[{\"id\":17,\"normalized\":0.42},{\"id\":999,\"normalized\":1}]}],\"voice\":[{\"slotId\":\"v0\",\"parameters\":[{\"id\":23,\"normalized\":0.61}]}]}"
              }
            }
            """;
        using var internet = new HttpClient(new StaticHandler(_ => string.Empty));
        using var ollama = new HttpClient(new StaticHandler(_ => tuningResult))
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/")
        };
        using var service = new SongEffectRecommendationService(internet, ollama);
        var guitarEffect = CreateEffect("guitar", "Guitar Rig", "Amp Simulator").Reference;
        var voiceEffect = CreateEffect("voice", "Vocal Rider", "Dynamics").Reference;
        var profile = new SongEffectProfile(
            "profile",
            "local:track",
            "Original aproximado",
            "Artist - Song",
            "Artist",
            "Song",
            DateTimeOffset.UtcNow,
            "qwen-test",
            "Propuesta",
            new SongInputEffectChain(
                0,
                "Guitarra mono",
                "Drive",
                [new SongEffectSlotRecommendation(guitarEffect, "Amp", "Drive", "Crunch")]),
            new SongInputEffectChain(
                1,
                "Voz mono",
                "Control",
                [new SongEffectSlotRecommendation(voiceEffect, "Dynamics", "Compresión", "Gentle")]));
        var catalog = new Dictionary<string, IReadOnlyList<Vst3ParameterDescriptor>>
        {
            [Vst3EffectItem.GetCatalogId(guitarEffect.ModulePath, guitarEffect.ClassId)] =
            [new Vst3ParameterDescriptor(17, "Drive", "Drive", "%", 0, 0.2d, "20%", 0.2d, "20%")],
            [Vst3EffectItem.GetCatalogId(voiceEffect.ModulePath, voiceEffect.ClassId)] =
            [new Vst3ParameterDescriptor(23, "Threshold", "Thresh", "dB", 0, 0.5d, "-18 dB", 0.5d, "-18 dB")]
        };

        var tuned = await service.TuneParametersAsync(profile, catalog);

        var guitarSetting = tuned.Guitar.Slots.Single().Effect.EffectiveParameterSettings.Single();
        var voiceSetting = tuned.Voice.Slots.Single().Effect.EffectiveParameterSettings.Single();
        Assert.AreEqual(17u, guitarSetting.Id);
        Assert.AreEqual(0.42d, guitarSetting.NormalizedValue);
        Assert.AreEqual(23u, voiceSetting.Id);
        Assert.AreEqual(0.61d, voiceSetting.NormalizedValue);
        StringAssert.Contains(tuned.Summary, "parámetros internos");
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
