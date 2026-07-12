using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class PlaybackNavigatorTests
{
    [TestMethod]
    public void Single_AutomaticEndStopsButManualNavigationStillWorks()
    {
        var navigator = new PlaybackNavigator(new Random(11))
        {
            Mode = PlaybackMode.Single
        };
        navigator.SetQueue(["a", "b", "c"], "a");

        Assert.IsNull(navigator.NextAutomatic());
        Assert.AreEqual("a", navigator.CurrentTrackId);
        Assert.AreEqual("b", navigator.NextManual());
        Assert.AreEqual("b", navigator.CurrentTrackId);
        Assert.AreEqual("a", navigator.Previous());
    }

    [TestMethod]
    public void Sequential_AdvancesInOrderStopsAtEndAndKeepsPreviousHistory()
    {
        var navigator = new PlaybackNavigator
        {
            Mode = PlaybackMode.Sequential
        };
        navigator.SetQueue(["a", "b", "c"], "a");

        Assert.AreEqual("b", navigator.NextAutomatic());
        Assert.AreEqual("c", navigator.NextAutomatic());
        Assert.IsNull(navigator.NextAutomatic());
        Assert.AreEqual("c", navigator.CurrentTrackId);
        Assert.AreEqual("b", navigator.Previous());
        Assert.AreEqual("a", navigator.Previous());
        Assert.IsNull(navigator.Previous());
    }

    [TestMethod]
    public void Shuffle_WithSeedVisitsEveryTrackOnceAndPreviousUsesHistory()
    {
        var first = CreateSeededShuffle(seed: 1729);
        var second = CreateSeededShuffle(seed: 1729);

        var firstSequence = Drain(first);
        var secondSequence = Drain(second);

        CollectionAssert.AreEqual(firstSequence, secondSequence);
        Assert.AreEqual(3, firstSequence.Distinct(StringComparer.Ordinal).Count());
        CollectionAssert.AreEquivalent(new[] { "b", "c", "d" }, firstSequence);
        Assert.IsNull(first.NextAutomatic());
        Assert.AreEqual(firstSequence[^2], first.Previous());
        Assert.AreEqual(firstSequence[^3], first.Previous());
        Assert.AreEqual("a", first.Previous());
        Assert.IsNull(first.Previous());
    }

    private static PlaybackNavigator CreateSeededShuffle(int seed)
    {
        var navigator = new PlaybackNavigator(new Random(seed))
        {
            Mode = PlaybackMode.Shuffle
        };
        navigator.SetQueue(["a", "b", "c", "d"], "a");
        return navigator;
    }

    private static string[] Drain(PlaybackNavigator navigator)
    {
        var sequence = new List<string>();
        string? next;
        while ((next = navigator.NextAutomatic()) is not null)
        {
            sequence.Add(next);
        }

        return sequence.ToArray();
    }
}
