using System;
using System.Collections.Generic;
using System.Linq;
using Prowl.Runtime;
using Prowl.Runtime.Resources;
using Prowl.Vector;

namespace Slopbox;

/// <summary>Bounded voices, preloaded resources, and separate levels for interface and world audio.</summary>
public sealed class GameAudio : Component
{
    private sealed class Voice
    {
        public AudioSource Source;
        public float Gain;
        public bool Ui;
    }

    private sealed class LoopVoice
    {
        public object Owner;
        public AudioSource Source;
        public float Gain, Fade;
        public bool Active, Ambient;
    }

    private readonly Dictionary<string, AudioClip> _clips = new();
    private readonly Dictionary<string, int> _lastVariant = new();
    private readonly Dictionary<string, double> _cooldowns = new();
    private readonly List<Voice> _voices = new();
    private readonly List<LoopVoice> _loops = new();
    private double _nextImpact;

    private static GameAudio Current
    {
        get
        {
            GameManager game = GameManager.Instance;
            return game.IsValid() ? game.Audio : null;
        }
    }

    public override void OnEnable()
    {
        GameManager game = GameManager.Instance;
        if (game.IsValid())
            foreach (AudioClip clip in game.Sounds)
                if (clip.IsValid()) _clips[clip.Name] = clip;
        for (int i = 0; i < 24; i++)
            _voices.Add(new Voice { Source = MakeSource("Sound Voice " + i), Ui = i < 8 });

    }

    public override void Start() => SetLoop(this, "ambient", true, default, false, 0.14f, true);

    private AudioSource MakeSource(string name)
    {
        var go = new GameObject(name);
        go.SetParent(GameObject, false);
        var source = go.AddComponent<AudioSource>();
        source.PlayOnStart = false;
        source.DopplerFactor = 0f;
        source.MinDistance = 2f;
        source.MaxDistance = 45f;
        return source;
    }

    private float Level(bool ui, bool ambient = false)
    {
        Settings s = GameManager.Instance.Settings;
        return s.MasterVolume * (ui ? s.UiVolume : ambient ? s.AmbienceVolume : s.EffectsVolume);
    }

    public override void Update()
    {
        foreach (Voice voice in _voices)
            voice.Source.Volume = voice.Gain * Level(voice.Ui);
        for (int i = _loops.Count - 1; i >= 0; i--)
        {
            LoopVoice loop = _loops[i];
            if (loop.Owner is EngineObject obj && obj.IsNotValid())
            {
                loop.Source.GameObject.Destroy();
                _loops.RemoveAt(i);
                continue;
            }
            float target = loop.Active ? 1f : 0f;
            loop.Fade += (target - loop.Fade) * MathF.Min(1f, Time.UnscaledDeltaTime * 14f);
            loop.Source.Volume = loop.Gain * loop.Fade * Level(false, loop.Ambient);
            if (loop.Fade < 0.005f && loop.Source.IsPlaying) loop.Source.Stop();
            else if (target > 0f && !loop.Source.IsPlaying) loop.Source.Play();
        }
    }

    public override void OnDisable()
    {
        foreach (Voice voice in _voices)
            if (voice.Source.IsValid()) voice.Source.GameObject.Destroy();
        foreach (LoopVoice loop in _loops)
            if (loop.Source.IsValid()) loop.Source.GameObject.Destroy();
        _voices.Clear();
        _loops.Clear();
        _clips.Clear();
    }

    public static void Play(string sound, float gain = 0.5f, float pitch = 1f, bool ui = false,
        Float3? position = null, float cooldown = 0.04f, float maxDistance = 45f, float minDistance = 2f)
    {
        GameAudio audio = Current;
        if (audio.IsNotValid()) return;
        double now = Time.TimeSinceStartup;
        if (audio._cooldowns.TryGetValue(sound, out double next) && now < next) return;
        if (!audio._clips.TryGetValue(sound, out AudioClip clip) || clip.IsNotValid()) return;
        Voice voice = audio._voices.FirstOrDefault(v => v.Ui == ui && !v.Source.IsPlaying && !v.Source.IsPaused);
        if (voice == null) return;
        audio._cooldowns[sound] = now + cooldown;
        voice.Gain = gain;
        voice.Source.Clip = clip;
        voice.Source.Spatial = position.HasValue;
        voice.Source.MinDistance = minDistance;
        voice.Source.MaxDistance = maxDistance;
        if (position.HasValue) voice.Source.Transform.Position = position.Value;
        voice.Source.Pitch = pitch;
        voice.Source.Volume = gain * audio.Level(ui);
        voice.Source.Play();
    }

    /// <summary>Plays a clip that is not one of the game's own sounds, such as a mounted game's footstep.</summary>
    public static void PlayClip(AudioClip clip, float gain, float pitch = 1f, Float3? position = null,
        float maxDistance = 30f, float minDistance = 2f)
    {
        GameAudio audio = Current;
        if (audio.IsNotValid() || clip.IsNotValid()) return;
        Voice voice = audio._voices.FirstOrDefault(v => !v.Ui && !v.Source.IsPlaying && !v.Source.IsPaused);
        if (voice == null) return;
        voice.Gain = gain;
        voice.Source.Clip = clip;
        voice.Source.Spatial = position.HasValue;
        voice.Source.MinDistance = minDistance;
        voice.Source.MaxDistance = maxDistance;
        if (position.HasValue) voice.Source.Transform.Position = position.Value;
        voice.Source.Pitch = pitch;
        voice.Source.Volume = gain * audio.Level(false);
        voice.Source.Play();
    }

    public static void Click() => Play("click", 0.3f, ui: true);
    public static void Hover() => Play("hover", 0.08f, 1.2f, true, cooldown: 0.09f);
    public static void Error() => Play("error", 0.32f, ui: true, cooldown: 0.15f);

    public static void Variant(string prefix, int count, float gain, Float3? position = null)
    {
        GameAudio audio = Current;
        if (audio.IsNotValid()) return;
        int index = Random.Shared.Next(count);
        if (audio._lastVariant.TryGetValue(prefix, out int previous) && index == previous)
            index = (index + 1) % count;
        audio._lastVariant[prefix] = index;
        Play(prefix + "_" + (index + 1), gain, 0.94f + Random.Shared.NextSingle() * 0.12f, position: position);
    }

    /// <summary>
    /// The game's own sound family for a surface, from a Source $surfaceprop such as "metal_box" or a material's
    /// name such as "concrete/concretefloor001a". Null if the text says nothing about it.
    /// </summary>
    public static string SurfaceOf(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.ToLowerInvariant();
        if (t.Contains("glass") || t.Contains("window")) return "glass";
        if (t.Contains("wood") || t.Contains("crate") || t.Contains("cardboard") || t.Contains("paper")) return "wood";
        if (t.Contains("grass") || t.Contains("foliage") || t.Contains("carpet") || t.Contains("flesh")) return "grass";
        if (t.Contains("gravel") || t.Contains("sand") || t.Contains("rock")) return "gravel";
        if (t.Contains("dirt") || t.Contains("mud") || t.Contains("nature") || t.Contains("slosh") || t.Contains("water")) return "dirt";
        if (t.Contains("metal") || t.Contains("grate") || t.Contains("vent") || t.Contains("chain") || t.Contains("pipe")
            || t.Contains("ladder") || t.Contains("computer") || t.Contains("canister") || t.Contains("barrel")) return "metal";
        if (t.Contains("concrete") || t.Contains("brick") || t.Contains("stone") || t.Contains("tile") || t.Contains("plaster")
            || t.Contains("asphalt") || t.Contains("pavement") || t.Contains("road") || t.Contains("cement")) return "concrete";
        return null;
    }

    /// <summary>Names of models/materials provide a fallback surface classification without changing assets.</summary>
    public static string Surface(GameObject go, string fallback = "concrete")
    {
        if (go.IsNotValid()) return fallback;
        Prop prop = go.GetComponentInParent<Prop>();
        string name = (go.Name + " " + (prop.IsValid() ? prop.SpawnId : "")
            + " " + (prop.IsValid() && prop.MaterialOverride.IsValid() ? prop.MaterialOverride.Name : "")).ToLowerInvariant();
        if (name.Contains("glass") || name.Contains("bottle")) return "glass";
        if (name.Contains("wood") || name.Contains("crate") || name.Contains("cardboard")) return "wood";
        if (name.Contains("grass")) return "grass";
        if (name.Contains("dirt") || name.Contains("soil")) return "dirt";
        if (name.Contains("gravel") || name.Contains("stone")) return "gravel";
        if (name.Contains("metal") || name.Contains("barrel") || name.Contains("car") || name.Contains("tool")
            || name.Contains("pipe") || name.Contains("thruster") || name.Contains("machine")) return "metal";
        if (name.Contains("floor")) return "grass";
        return fallback;
    }

    public static bool Impact(Prop prop, Collision collision)
    {
        GameAudio audio = Current;
        if (audio.IsNotValid() || prop.Body.IsNotValid() || prop.IsFrozen) return false;
        // A prop pair reports the contact twice. Pick one side, and cap all impacts to 20 per second.
        if (collision.Rigidbody.IsValid() && collision.Rigidbody.GameObject.GetComponent<Prop>().IsValid()
            && prop.GameObject.Identifier.CompareTo(collision.Rigidbody.GameObject.Identifier) > 0) return false;
        float strength = collision.ImpulseMagnitude / MathF.Max(1f, MathF.Sqrt(prop.Body.Mass) * 0.25f);
        if (strength < 0.7f || Time.TimeSinceStartup < audio._nextImpact) return false;
        audio._nextImpact = Time.TimeSinceStartup + 0.05;
        string surface = Surface(prop.GameObject);
        if (surface is "grass" or "dirt" or "gravel") surface = "concrete";
        int count = surface == "wood" ? 2 : surface == "metal" ? 4 : 3;
        Variant("impact_" + surface, count, Math.Clamp(strength * 0.07f, 0.08f, 0.7f), collision.Point);
        return true;
    }

    public static void SetLoop(object owner, string sound, bool active, Float3 position = default,
        bool spatial = false, float gain = 0.12f, bool ambient = false, float maxDistance = 45f)
    {
        GameAudio audio = Current;
        if (audio.IsNotValid()) return;
        audio._clips.TryGetValue(sound, out AudioClip clip);
        SetLoop(owner, clip, active, position, spatial, gain, ambient, maxDistance);
    }

    /// <summary>A loop of a clip that is not one of the game's own sounds, such as one made in code.</summary>
    public static void SetLoop(object owner, AudioClip clip, bool active, Float3 position = default,
        bool spatial = false, float gain = 0.12f, bool ambient = false, float maxDistance = 45f)
    {
        GameAudio audio = Current;
        if (audio.IsNotValid()) return;
        LoopVoice loop = audio._loops.FirstOrDefault(v => ReferenceEquals(v.Owner, owner));
        if (loop == null)
        {
            if (!active || clip.IsNotValid()) return;
            loop = new LoopVoice { Owner = owner, Source = audio.MakeSource(clip.Name + " Loop"), Ambient = ambient };
            loop.Source.Clip = clip;
            loop.Source.Loop = true;
            loop.Source.Spatial = spatial;
            audio._loops.Add(loop);
        }
        loop.Source.Transform.Position = position;
        loop.Source.MaxDistance = maxDistance;
        loop.Active = active;
        loop.Gain = gain;
    }

    /// <summary>Release a transient emitter immediately, such as a rocket that just exploded.</summary>
    public static void StopLoop(object owner)
    {
        GameAudio audio = Current;
        if (audio.IsNotValid()) return;
        for (int i = audio._loops.Count - 1; i >= 0; i--)
        {
            LoopVoice loop = audio._loops[i];
            if (!ReferenceEquals(loop.Owner, owner)) continue;
            if (loop.Source.IsValid())
            {
                loop.Source.Stop();
                loop.Source.GameObject.Destroy();
            }
            audio._loops.RemoveAt(i);
        }
    }
}
