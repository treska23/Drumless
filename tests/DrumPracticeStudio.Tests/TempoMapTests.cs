using DrumPracticeStudio.Models;
using DrumPracticeStudio.Services;
using NAudio.Wave;

namespace DrumPracticeStudio.Tests;

[TestClass]
public sealed class TempoMapTests
{
    [TestMethod]
    public void Normalize_SortsSegmentsAndUsesFirstAsCompatibilityTempo()
    {
        var tempo = TempoSettings.Normalize(new TempoSettings(
            100d,
            0.2d,
            Segments:
            [
                TempoSegment.Create(30d, 90d, 30d, sourceName: "Manual"),
                TempoSegment.Create(0d, 120d, 0.25d, sourceName: "Fuente")
            ]));

        Assert.AreEqual(2, tempo.EffectiveSegments.Count);
        Assert.AreEqual(0d, tempo.EffectiveSegments[0].StartSeconds);
        Assert.AreEqual(120d, tempo.Bpm);
        Assert.AreEqual(0.25d, tempo.FirstBeatSeconds);
        Assert.AreEqual(90d, tempo.GetSegmentAt(45d).Bpm);
    }

    [TestMethod]
    public void Grid_ChangesSubdivisionLengthAtSegmentBoundary()
    {
        var tempo = TempoSettings.Normalize(new TempoSettings(
            120d,
            0d,
            Segments:
            [
                TempoSegment.Create(0d, 120d, 0d),
                TempoSegment.Create(10d, 60d, 10d)
            ]));

        Assert.AreEqual(0d, TempoGrid.NearestGridErrorSeconds(9.875d, tempo), 1e-9d);
        Assert.AreEqual(0d, TempoGrid.NearestGridErrorSeconds(10.25d, tempo), 1e-9d);
        Assert.AreEqual(0.05d, TempoGrid.NearestGridErrorSeconds(10.30d, tempo), 1e-9d);
    }

    [TestMethod]
    public void AnalyzeMap_ProposesBothTemposInSyntheticVariableTrack()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.Combine("variable-tempo.wav");
        WriteVariableClickTrack(path);

        var result = new TempoAnalysisService().AnalyzeMap(path);

        Assert.IsTrue(
            result.Segments.Any(segment => Math.Abs(segment.Bpm - 120d) < 3d),
            string.Join(", ", result.Segments.Select(segment => segment.Bpm)));
        Assert.IsTrue(
            result.Segments.Any(segment => Math.Abs(segment.Bpm - 90d) < 3d),
            string.Join(", ", result.Segments.Select(segment => segment.Bpm)));
        Assert.IsTrue(result.Segments.Count >= 2);
    }

    private static void WriteVariableClickTrack(string path)
    {
        const int sampleRate = 48_000;
        const double durationSeconds = 72d;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, 1);
        using var writer = new WaveFileWriter(path, format);
        var samples = new float[(int)(durationSeconds * sampleRate)];
        WriteClicks(samples, sampleRate, start: 0.25d, end: 36d, bpm: 120d);
        WriteClicks(samples, sampleRate, start: 36d, end: durationSeconds, bpm: 90d);
        writer.WriteSamples(samples, 0, samples.Length);
    }

    private static void WriteClicks(
        float[] samples,
        int sampleRate,
        double start,
        double end,
        double bpm)
    {
        var beatSeconds = 60d / bpm;
        for (var beat = start; beat < end; beat += beatSeconds)
        {
            var frame = (int)Math.Round(beat * sampleRate);
            for (var offset = 0; offset < sampleRate / 50 && frame + offset < samples.Length; offset++)
            {
                var envelope = Math.Exp(-offset / (sampleRate * 0.004d));
                samples[frame + offset] = (float)(
                    Math.Sin(offset * 2d * Math.PI * 1_500d / sampleRate) * envelope);
            }
        }
    }
}
