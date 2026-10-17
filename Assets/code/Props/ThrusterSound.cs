using System;
using System.Collections.Generic;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Slopbox;

/// <summary>
/// The roar of a <see cref="ThrusterProp"/>, synthesized instead of loaded: filtered noise with a flutter and a low
/// combustion tone, deeper and heavier the more force the thruster has. Clips are made once per size and shared.
/// </summary>
public static class ThrusterSound
{
    private const int Rate = 22050;
    private const int Sizes = 6;

    private static readonly Dictionary<int, AudioClip> s_clips = new();

    /// <summary>0 for the weakest thruster the tool makes (10 N), 1 for the strongest (10000 N).</summary>
    public static float Size(float force) => Math.Clamp((MathF.Log10(MathF.Max(force, 1f)) - 1f) / 3f, 0f, 1f);

    public static AudioClip Clip(float force)
    {
        int bucket = (int)MathF.Round(Size(force) * (Sizes - 1));
        if (s_clips.TryGetValue(bucket, out AudioClip clip) && clip.IsValid()) return clip;
        clip = Synthesize(bucket / (float)(Sizes - 1), bucket);
        s_clips[bucket] = clip;
        return clip;
    }

    private static AudioClip Synthesize(float size, int seed)
    {
        var random = new Random(1234 + seed);
        int fade = Rate / 4;
        int length = Rate * 2 + fade;
        var samples = new float[length];

        float roarA = Pole(Lerp(900f, 220f, size)), hissA = Pole(Lerp(2600f, 1200f, size)), flutterA = Pole(Lerp(40f, 18f, size));
        float hissGain = Lerp(0.55f, 0.18f, size), toneHz = Lerp(140f, 42f, size);
        float roar1 = 0f, roar2 = 0f, hiss = 0f, flutter = 0f, phase = 0f;

        for (int i = 0; i < length; i++)
        {
            float white = random.NextSingle() * 2f - 1f;
            roar1 += roarA * (white - roar1);
            roar2 += roarA * (roar1 - roar2);
            hiss += hissA * (white - hiss);
            flutter += flutterA * (random.NextSingle() * 2f - 1f - flutter);

            phase += MathF.Tau * toneHz * (1f + flutter * 0.3f) / Rate;
            float tone = MathF.Sin(phase) * 0.12f;
            float body = roar2 * 4f + (white - hiss) * hissGain + tone;
            samples[i] = body * (1f + flutter * 4f);
        }

        // Fold the tail over the head so the loop has no seam.
        var loop = new float[length - fade];
        Array.Copy(samples, loop, loop.Length);
        for (int i = 0; i < fade; i++)
        {
            float t = i / (float)fade;
            loop[i] = samples[length - fade + i] * (1f - t) + samples[i] * t;
        }

        float peak = 0f;
        foreach (float s in loop) peak = MathF.Max(peak, MathF.Abs(s));
        float scale = peak > 0f ? 0.8f / peak : 1f;
        for (int i = 0; i < loop.Length; i++) loop[i] *= scale;

        AudioClip clip = AudioClip.Create("thruster_" + seed, loop, 1, Rate);
        if (clip != null) clip.ClipName = "thruster_" + seed;
        return clip;
    }

    private static float Pole(float cutoff) => 1f - MathF.Exp(-MathF.Tau * cutoff / Rate);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}
