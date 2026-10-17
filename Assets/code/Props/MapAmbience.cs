using System;
using System.Collections.Generic;
using System.Linq;
using Prowl.Echo;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;
using Slopbox.Mounting;

namespace Slopbox;

/// <summary>
/// Plays a mounted map's sounds while it is in the scene: its looping ambient sounds where the map put them, and
/// the soundscape of whichever area the listener is in, its loops and its sounds now and then, faded across as
/// the listener moves between areas.
/// </summary>
[DisallowMultipleComponent]
public sealed class MapAmbience : Component
{
    /// <summary>Seconds a soundscape takes to fade in or out.</summary>
    private const float FadeTime = 2f;

    private sealed class Voice
    {
        public AudioSource Source;
        public float Gain, Fade, Target;
    }

    private sealed class Playing
    {
        public Soundscape Soundscape;
        public readonly List<Voice> Loops = new();
        public float[] NextRandom;
        public float Fade, Target;
    }

    [SerializeIgnore] private MountedMap _map;
    [SerializeIgnore] private GameObject _geometry;
    [SerializeIgnore] private readonly List<Voice> _ambient = new();
    [SerializeIgnore] private readonly List<Playing> _soundscapes = new();
    [SerializeIgnore] private readonly List<AudioSource> _oneShots = new();
    [SerializeIgnore] private MountedMap.SoundscapeZone _zone;
    [SerializeIgnore] private float _nextZoneCheck;

    internal void Bind(MountedMap map, GameObject geometry)
    {
        _map = map;
        _geometry = geometry;
    }

    private static float Level
    {
        get
        {
            GameManager game = GameManager.Instance;
            if (game.IsNotValid() || game.Settings == null) return 0.7f;
            return game.Settings.MasterVolume * game.Settings.EffectsVolume;
        }
    }

    public override void Start()
    {
        if (_map?.Audio == null || _geometry.IsNotValid()) return;

        foreach (MountedMap.PlacedSound sound in _map.Sounds)
        {
            if (_map.Audio.Find(sound.Sound) is not { } clip) continue;
            AudioSource source = MakeSource($"Sound {sound.Sound}", clip.Clip, loop: true);
            source.Transform.LocalPosition = sound.Position;
            source.Spatial = !sound.Everywhere;
            source.MinDistance = Math.Min(1.5f, sound.Radius * 0.25f);
            source.MaxDistance = sound.Radius;
            source.Pitch = sound.Pitch * clip.Pitch;
            var voice = new Voice { Source = source, Gain = sound.Volume * clip.Volume, Fade = 1f, Target = 1f };
            source.Volume = voice.Gain * Level;
            source.Play();
            _ambient.Add(voice);
        }
    }

    private AudioSource MakeSource(string name, AudioClip clip, bool loop)
    {
        var go = new GameObject(name);
        go.SetParent(_geometry, false);
        var source = go.AddComponent<AudioSource>();
        source.PlayOnStart = false;
        source.DopplerFactor = 0f;
        source.Clip = clip;
        source.Loop = loop;
        return source;
    }

    public override void Update()
    {
        if (_map?.Audio == null || _geometry.IsNotValid()) return;
        float level = Level;

        foreach (Voice voice in _ambient)
            if (voice.Source.IsValid()) voice.Source.Volume = voice.Gain * level;

        if (Time.TimeSinceStartup >= _nextZoneCheck)
        {
            _nextZoneCheck = (float)Time.TimeSinceStartup + 0.25f;
            UpdateZone();
        }

        float dt = Time.DeltaTime;
        for (int i = _soundscapes.Count - 1; i >= 0; i--)
        {
            Playing playing = _soundscapes[i];
            playing.Fade = MoveTowards(playing.Fade, playing.Target, dt / FadeTime);
            foreach (Voice loop in playing.Loops)
                if (loop.Source.IsValid()) loop.Source.Volume = loop.Gain * playing.Fade * level;

            if (playing.Target <= 0f && playing.Fade <= 0f)
            {
                foreach (Voice loop in playing.Loops)
                    if (loop.Source.IsValid()) loop.Source.GameObject.Destroy();
                _soundscapes.RemoveAt(i);
                continue;
            }

            if (playing.Target > 0f) PlayRandoms(playing, level);
        }

        for (int i = _oneShots.Count - 1; i >= 0; i--)
        {
            AudioSource shot = _oneShots[i];
            if (shot.IsValid() && shot.IsPlaying) continue;
            if (shot.IsValid()) shot.GameObject.Destroy();
            _oneShots.RemoveAt(i);
        }
    }

    private static float MoveTowards(float value, float target, float step)
        => value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);

    /// <summary>The soundscape around the listener: the nearest zone that reaches it, as the game picks.</summary>
    private void UpdateZone()
    {
        if (_map.Soundscapes.Count == 0) return;
        AudioListener listener = Scene.Current.FindObjectsOfType<AudioListener>().FirstOrDefault();
        if (listener.IsNotValid()) return;

        Float3 here = _geometry.Transform.InverseTransformPoint(listener.Transform.Position);
        MountedMap.SoundscapeZone best = null;
        float bestDistance = float.MaxValue;
        foreach (MountedMap.SoundscapeZone zone in _map.Soundscapes)
        {
            float distance = Float3.Distance(here, zone.Position);
            if (zone.Radius >= 0f && distance > zone.Radius) continue;
            if (distance < bestDistance)
            {
                best = zone;
                bestDistance = distance;
            }
        }

        // Out of every zone, the last one keeps playing, as in the game.
        if (best == null || best.Name.Equals(_zone?.Name, StringComparison.OrdinalIgnoreCase)) return;
        _zone = best;

        foreach (Playing playing in _soundscapes) playing.Target = 0f;
        Soundscape soundscape = _map.Audio.Soundscape(best.Name);
        if (soundscape == null)
        {
            Debug.LogWarning($"Soundscape {best.Name} was not found");
            return;
        }

        var next = new Playing { Soundscape = soundscape, Target = 1f, NextRandom = new float[soundscape.Randoms.Count] };
        float now = (float)Time.TimeSinceStartup;
        for (int i = 0; i < next.NextRandom.Length; i++)
            next.NextRandom[i] = now + Between(soundscape.Randoms[i].MinTime, soundscape.Randoms[i].MaxTime);

        foreach (Soundscape.Loop loop in soundscape.Loops)
        {
            if (_map.Audio.Find(loop.Sound) is not { } clip) continue;
            AudioSource source = MakeSource($"Soundscape {loop.Sound}", clip.Clip, loop: true);
            source.Spatial = false;
            source.Pitch = loop.Pitch * clip.Pitch;
            source.Volume = 0f;
            source.Play();
            next.Loops.Add(new Voice { Source = source, Gain = loop.Volume * clip.Volume });
        }
        _soundscapes.Add(next);
    }

    private void PlayRandoms(Playing playing, float level)
    {
        float now = (float)Time.TimeSinceStartup;
        for (int i = 0; i < playing.NextRandom.Length; i++)
        {
            if (now < playing.NextRandom[i]) continue;
            Soundscape.Random random = playing.Soundscape.Randoms[i];
            playing.NextRandom[i] = now + Between(random.MinTime, random.MaxTime);
            if (_oneShots.Count >= 8) continue;

            string sound = random.Sounds[Random.Shared.Next(random.Sounds.Count)];
            if (_map.Audio.Find(sound) is not { } clip) continue;
            AudioSource source = MakeSource($"Soundscape {sound}", clip.Clip, loop: false);
            source.Spatial = false;
            source.Pitch = Between(random.MinPitch, random.MaxPitch) * clip.Pitch;
            source.Volume = Between(random.MinVolume, random.MaxVolume) * clip.Volume * playing.Fade * level;
            source.Play();
            _oneShots.Add(source);
        }
    }

    private static float Between(float min, float max) => min + (max - min) * Random.Shared.NextSingle();

    public override void OnDisable() => StopAll();

    private void StopAll()
    {
        foreach (Voice voice in _ambient)
            if (voice.Source.IsValid()) voice.Source.GameObject.Destroy();
        foreach (Playing playing in _soundscapes)
            foreach (Voice loop in playing.Loops)
                if (loop.Source.IsValid()) loop.Source.GameObject.Destroy();
        foreach (AudioSource shot in _oneShots)
            if (shot.IsValid()) shot.GameObject.Destroy();
        _ambient.Clear();
        _soundscapes.Clear();
        _oneShots.Clear();
        _zone = null;
    }
}
