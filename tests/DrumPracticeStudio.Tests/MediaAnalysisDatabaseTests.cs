using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class MediaAnalysisDatabaseTests
{
    [TestMethod]
    public void SnapshotAndLoad_PreserveTempoOriginAndPerformanceHistory()
    {
        var database = new MediaAnalysisDatabase();
        var tempo = new TempoSettings(128d, 0.42d, 4, true, 0.5d, 0.87d);
        var result = new DrumPerformanceResult(100, 91, 4, 5, 91d, 18d, 73d);
        var finishedAt = new DateTimeOffset(2026, 7, 16, 10, 30, 0, TimeSpan.Zero);

        database.SetTempo(
            "local:track-a",
            tempo,
            TempoAnalysisOrigin.Automatic,
            finishedAt.AddMinutes(-5));
        database.AddPerformanceSession(
            "local:track-a",
            DrumPerformanceSession.Create(result, 21.5d, true, finishedAt));

        var restored = new MediaAnalysisDatabase();
        restored.Load(database.Snapshot());
        var record = restored.Get("local:track-a");

        Assert.IsNotNull(record);
        Assert.AreEqual(128d, record.Tempo?.Bpm);
        Assert.AreEqual(TempoAnalysisOrigin.Automatic, record.TempoOrigin);
        Assert.AreEqual(1, record.PerformanceSessions.Count);
        Assert.AreEqual(91d, record.PerformanceSessions[0].AccuracyPercent);
        Assert.AreEqual(21.5d, record.PerformanceSessions[0].LatencyCompensationMilliseconds);
        Assert.IsTrue(record.PerformanceSessions[0].FinishedAtNaturalEnd);
    }

    [TestMethod]
    public void YouTubeKey_IsSharedAndRemovingPlaylistReferenceDoesNotRemoveAnalysis()
    {
        var database = new MediaAnalysisDatabase();
        database.SetTempo(
            "youtube:video123",
            new TempoSettings(99d, 1.1d),
            TempoAnalysisOrigin.Manual);
        var firstPlaylistItem = new PlaylistItem
        {
            Id = "first",
            Kind = PlaylistItemKind.YouTube,
            YouTubeVideoId = "video123",
            YouTubeUrl = "https://www.youtube.com/watch?v=video123",
            Title = "Video"
        };
        var secondPlaylistItem = new PlaylistItem
        {
            Id = "second",
            Kind = PlaylistItemKind.YouTube,
            YouTubeVideoId = "video123",
            YouTubeUrl = "https://www.youtube.com/watch?v=video123",
            Title = "Video"
        };
        var playlist = new Playlist { Id = "playlist", Name = "Practice" };
        playlist.Items.Add(firstPlaylistItem);
        playlist.Items.Add(secondPlaylistItem);

        Assert.AreEqual(firstPlaylistItem.MediaKey, secondPlaylistItem.MediaKey);
        Assert.AreEqual(99d, database.GetTempo(secondPlaylistItem.MediaKey)?.Bpm);
        Assert.IsTrue(PlaylistEditor.RemoveItem(playlist, firstPlaylistItem.Id));
        Assert.IsNotNull(database.Get(firstPlaylistItem.MediaKey));
        Assert.IsTrue(database.Remove(firstPlaylistItem.MediaKey));
        Assert.IsNull(database.Get(secondPlaylistItem.MediaKey));
    }
}
