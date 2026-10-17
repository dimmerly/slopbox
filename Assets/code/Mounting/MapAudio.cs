using System.Collections.Generic;
using Prowl.Runtime.Resources;

namespace Slopbox.Mounting;

/// <summary>A sound ready to play: the clip, and the volume and pitch its game gives it.</summary>
public readonly record struct MapSoundClip(AudioClip Clip, float Volume, float Pitch);

/// <summary>Where a map's sounds come from.</summary>
public interface IMapAudio
{
    /// <summary>
    /// A sound by its name in the game: a file below sound/ ("ambient/water/leak_1.wav") or a sound script entry
    /// ("pipe_water_drip"), which picks one of its variants each time. Null if it cannot be found.
    /// </summary>
    MapSoundClip? Find(string sound);

    /// <summary>A soundscape by name, with the soundscapes it plays inside it folded in. Null if missing.</summary>
    Soundscape Soundscape(string name);
}

/// <summary>The background sound of an area: sounds looping all the time, and others now and then.</summary>
public sealed class Soundscape
{
    public sealed record Loop(string Sound, float Volume, float Pitch);

    /// <summary>One of <paramref name="Sounds"/> every so often, with times in seconds and pitch as a rate.</summary>
    public sealed record Random(IReadOnlyList<string> Sounds, float MinTime, float MaxTime,
        float MinVolume, float MaxVolume, float MinPitch, float MaxPitch);

    public string Name { get; init; }
    public List<Loop> Loops { get; } = new();
    public List<Random> Randoms { get; } = new();
}
