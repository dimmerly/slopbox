using System;
using System.Collections.Generic;
using System.Linq;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Slopbox.Mounting.Source;

/// <summary>
/// The footstep sounds the mounted Source games give each surface: the surface's step sound script entry from
/// its surfaceproperties, then one of that entry's waves. Clips are kept once loaded, as footsteps repeat.
/// </summary>
public static class SourceFootsteps
{
    private static readonly Dictionary<(SourceMount, string), AudioClip> s_clips = new();

    /// <summary>
    /// A footstep for <paramref name="surface"/> (a $surfaceprop such as "concrete"), from the first mounted
    /// Source game that has one, for the left or right foot. Null if none does.
    /// </summary>
    public static MapSoundClip? Find(string surface, bool left)
    {
        if (string.IsNullOrWhiteSpace(surface)) return null;
        foreach (SourceMount mount in Mounts())
        {
            string script = mount.StepSound(surface, left) ?? mount.StepSound("default", left);
            if (script == null) continue;
            SoundEntry entry = mount.Sounds.Find(script);
            if (entry == null || entry.Waves.Count == 0) continue;

            string wave = SourceAudio.CleanName(entry.Waves[Random.Shared.Next(entry.Waves.Count)]);
            if (!s_clips.TryGetValue((mount, wave), out AudioClip clip) || clip.IsNotValid())
            {
                clip = SourceAudio.Clip(wave, mount.ReadFile("sound/" + wave));
                s_clips[(mount, wave)] = clip;
            }
            if (clip.IsNotValid()) continue;

            float volume = entry.MinVolume + (entry.MaxVolume - entry.MinVolume) * Random.Shared.NextSingle();
            float pitch = entry.MinPitch + (entry.MaxPitch - entry.MinPitch) * Random.Shared.NextSingle();
            return new MapSoundClip(clip, volume, pitch);
        }
        return null;
    }

    /// <summary>Half-Life 2 first, whose footsteps the other games share, then the rest.</summary>
    private static IEnumerable<SourceMount> Mounts()
        => MountDirectory.All.OfType<SourceMount>().Where(m => m.IsMounted).OrderBy(m => m.Ident == "hl2" ? 0 : 1);

    /// <summary>Drops a mount's clips when it unmounts.</summary>
    internal static void Forget(SourceMount mount)
    {
        foreach ((SourceMount, string) key in s_clips.Keys.Where(k => k.Item1 == mount).ToList())
        {
            if (s_clips[key].IsValid()) s_clips[key].Dispose();
            s_clips.Remove(key);
        }
    }
}
