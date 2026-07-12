using System.Text;

namespace DrumPracticeStudio.Services;

internal static class FactorySoundGenerator
{
    private const int SampleRate = 48_000;

    public static void EnsureSample(
        string path,
        string voice,
        bool electronic,
        bool hard,
        int variation)
    {
        if (File.Exists(path))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var samples = Synthesize(voice, electronic, hard, variation);
        WriteMonoPcm16(path, samples);
    }

    private static float[] Synthesize(string voice, bool electronic, bool hard, int variation)
    {
        var duration = (voice, electronic) switch
        {
            ("kick.main", true) => 0.82,
            ("snare.center", true) => 0.32,
            ("hihat.closed", _) => 0.13,
            ("hihat.open", true) => 0.48,
            ("hihat.open", false) => 0.75,
            ("crash.edge", true) => 0.72,
            ("crash.edge", false) => 1.55,
            ("ride.bow", true) => 0.58,
            ("ride.bow", false) => 1.1,
            ("kick.main", false) => 0.45,
            _ => 0.55
        };

        var result = new float[(int)(SampleRate * duration)];
        var random = new Random(StableSeed($"{voice}:{electronic}:{hard}:{variation}"));
        var gain = hard ? 0.92f : 0.56f;
        var previousNoise = 0f;

        for (var index = 0; index < result.Length; index++)
        {
            var t = index / (double)SampleRate;
            var noise = (float)(random.NextDouble() * 2d - 1d);
            var highNoise = noise - previousNoise * 0.82f;
            previousNoise = noise;

            double sample = voice switch
            {
                "kick.main" => Kick(t, noise, electronic, variation),
                "snare.center" => Snare(t, highNoise, electronic, variation),
                "hihat.closed" => Hat(t, highNoise, electronic, open: false),
                "hihat.open" => Hat(t, highNoise, electronic, open: true),
                "tom.low" => Tom(t, low: true, electronic, variation),
                "tom.high" => Tom(t, low: false, electronic, variation),
                "crash.edge" => Cymbal(t, highNoise, ride: false, electronic),
                "ride.bow" => Cymbal(t, highNoise, ride: true, electronic),
                _ => noise * Math.Exp(-t * 10d)
            };

            if (electronic)
            {
                sample = Math.Tanh(sample * 1.35d) * 0.88d;
            }

            result[index] = Math.Clamp((float)sample * gain, -0.98f, 0.98f);
        }

        return result;
    }

    private static double Kick(double t, float noise, bool electronic, int variation)
    {
        if (electronic)
        {
            var electronicPitch = 42d + 230d * Math.Exp(-t * 28d);
            var sub = Math.Sin(2d * Math.PI * electronicPitch * t) * Math.Exp(-t * 4.7d);
            var electronicClick = Math.Sign(Math.Sin(2d * Math.PI * 1_850d * t)) * Math.Exp(-t * 95d) * 0.28d;
            return sub + electronicClick;
        }

        var startFrequency = electronic ? 185d : 125d;
        var endFrequency = electronic ? 48d : 54d;
        var pitch = endFrequency + (startFrequency - endFrequency) * Math.Exp(-t * 24d);
        var body = Math.Sin(2d * Math.PI * pitch * t + variation * 0.04d) * Math.Exp(-t * (electronic ? 8d : 10d));
        var click = noise * Math.Exp(-t * 90d) * (electronic ? 0.38d : 0.22d);
        return body + click;
    }

    private static double Snare(double t, float noise, bool electronic, int variation)
    {
        if (electronic)
        {
            var burstEnvelope = Math.Exp(-t * 18d) +
                                (t > 0.018d ? Math.Exp(-(t - 0.018d) * 32d) * 0.72d : 0d) +
                                (t > 0.036d ? Math.Exp(-(t - 0.036d) * 38d) * 0.48d : 0d);
            var snap = Math.Sin(2d * Math.PI * 235d * t) * Math.Exp(-t * 22d) * 0.62d;
            return noise * burstEnvelope * 0.58d + snap;
        }

        var toneFrequency = electronic ? 225d : 178d + variation * 4d;
        var body = Math.Sin(2d * Math.PI * toneFrequency * t) * Math.Exp(-t * 14d) * 0.42d;
        var wires = noise * Math.Exp(-t * (electronic ? 18d : 11d)) * 0.82d;
        return body + wires;
    }

    private static double Hat(double t, float noise, bool electronic, bool open)
    {
        if (electronic)
        {
            var decayElectronic = open ? 9d : 54d;
            var digitalMetal = Math.Sign(Math.Sin(2d * Math.PI * 6_113d * t)) * 0.28d +
                               Math.Sign(Math.Sin(2d * Math.PI * 8_731d * t)) * 0.22d +
                               Math.Sin(2d * Math.PI * 12_221d * t) * 0.18d;
            return (digitalMetal + noise * 0.34d) * Math.Exp(-t * decayElectronic);
        }

        var decay = open ? (electronic ? 5.3d : 4.4d) : (electronic ? 42d : 34d);
        var metallic = Math.Sin(2d * Math.PI * 7_900d * t) * 0.18d +
                       Math.Sin(2d * Math.PI * 10_700d * t) * 0.12d;
        return (noise * 0.78d + metallic) * Math.Exp(-t * decay);
    }

    private static double Tom(double t, bool low, bool electronic, int variation)
    {
        var baseFrequency = low ? 92d : 142d;
        if (electronic)
        {
            baseFrequency *= 1.14d;
        }

        var pitch = baseFrequency + (electronic ? 125d : 42d) * Math.Exp(-t * (electronic ? 15d : 20d));
        var tone = Math.Sin(2d * Math.PI * pitch * t + variation * 0.03d);
        return (electronic ? Math.Tanh(tone * 1.8d) : tone) * Math.Exp(-t * (electronic ? 11d : 7.5d));
    }

    private static double Cymbal(double t, float noise, bool ride, bool electronic)
    {
        if (electronic)
        {
            var carrier = Math.Sin(2d * Math.PI * (ride ? 3_900d : 2_800d) * t +
                                   2.6d * Math.Sin(2d * Math.PI * 137d * t));
            var gated = Math.Sign(Math.Sin(2d * Math.PI * (ride ? 7_300d : 5_200d) * t));
            return (carrier * 0.42d + gated * 0.27d + noise * 0.22d) * Math.Exp(-t * (ride ? 7d : 5.5d));
        }

        var decay = ride ? 2.8d : 2.1d;
        var ring = Math.Sin(2d * Math.PI * (ride ? 5_900d : 4_300d) * t) * 0.14d +
                   Math.Sin(2d * Math.PI * (ride ? 8_300d : 7_100d) * t) * 0.11d;
        var attack = Math.Exp(-t * 65d) * (ride ? 0.55d : 0.8d);
        return (noise * 0.55d + ring + attack) * Math.Exp(-t * decay) * (electronic ? 0.9d : 1d);
    }

    private static int StableSeed(string value)
    {
        unchecked
        {
            var hash = 17;
            foreach (var character in value)
            {
                hash = hash * 31 + character;
            }

            return hash;
        }
    }

    private static void WriteMonoPcm16(string path, float[] samples)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        const short channels = 1;
        const short bitsPerSample = 16;
        var dataLength = samples.Length * sizeof(short);

        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        foreach (var sample in samples)
        {
            writer.Write((short)Math.Clamp(sample * short.MaxValue, short.MinValue, short.MaxValue));
        }
    }
}
