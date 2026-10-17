using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Slopbox.Mounting.Source;

/// <summary>A sound file of a Source game (.wav or .mp3).</summary>
internal sealed class SourceSoundLoader(SourceFile file) : ResourceLoader<SourceMount>
{
    protected override object Load() => SourceAudio.Clip(Name, file.Read());
}

/// <summary>
/// Makes clips from Source sound files. WAVs are decoded here, as the games use Microsoft ADPCM for most of
/// them alongside plain PCM, and often carry extra chunks; anything else is handed to Prowl to decode.
/// </summary>
internal static class SourceAudio
{
    private static readonly int[] s_adaptation = [230, 230, 230, 230, 307, 409, 512, 614, 768, 614, 512, 409, 307, 230, 230, 230];

    /// <summary>The characters sound names start with to tell the engine how to play them, such as ")" or "^".</summary>
    private const string Prefixes = "*#@<>^)(}$!?&~`+%";

    /// <summary>"^)Ambient\Water\Leak_1.wav" becomes "ambient/water/leak_1.wav".</summary>
    public static string CleanName(string sound) => ContentPaths.Normalize(sound.Trim().TrimStart(Prefixes.ToCharArray()));

    public static AudioClip Clip(string name, byte[] data)
    {
        if (data == null || data.Length < 12) return null;

        if (Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && Encoding.ASCII.GetString(data, 8, 4) == "WAVE")
        {
            AudioClip decoded = DecodeWav(name, data);
            if (decoded != null) return decoded;
        }

        return new AudioClip(data, isUnique: true) { ClipName = name };
    }

    private static AudioClip DecodeWav(string name, byte[] data)
    {
        int format = 0, channels = 0, rate = 0, blockAlign = 0, bits = 0, samplesPerBlock = 0;
        short[] coef1 = null, coef2 = null;
        int dataAt = -1, dataLength = 0;

        int at = 12;
        while (at + 8 <= data.Length)
        {
            string id = Encoding.ASCII.GetString(data, at, 4);
            int size = BitConverter.ToInt32(data, at + 4);
            int body = at + 8;
            if (size < 0) break;
            size = Math.Min(size, data.Length - body);

            if (id == "fmt " && size >= 16)
            {
                format = BitConverter.ToUInt16(data, body);
                channels = BitConverter.ToUInt16(data, body + 2);
                rate = BitConverter.ToInt32(data, body + 4);
                blockAlign = BitConverter.ToUInt16(data, body + 12);
                bits = BitConverter.ToUInt16(data, body + 14);
                if (format == 2 && size >= 22)
                {
                    samplesPerBlock = BitConverter.ToUInt16(data, body + 18);
                    int count = BitConverter.ToUInt16(data, body + 20);
                    coef1 = new short[count];
                    coef2 = new short[count];
                    for (int i = 0; i < count && body + 22 + i * 4 + 4 <= data.Length; i++)
                    {
                        coef1[i] = BitConverter.ToInt16(data, body + 22 + i * 4);
                        coef2[i] = BitConverter.ToInt16(data, body + 24 + i * 4);
                    }
                }
            }
            else if (id == "data")
            {
                dataAt = body;
                dataLength = size;
            }

            at = body + size + (size & 1);
        }

        if (dataAt < 0 || channels is < 1 or > 2 || rate <= 0) return null;

        float[] samples = format switch
        {
            1 when bits == 16 => Pcm16(data, dataAt, dataLength),
            1 when bits == 8 => Pcm8(data, dataAt, dataLength),
            2 when coef1 != null && blockAlign > 0 => MsAdpcm(data, dataAt, dataLength, channels, blockAlign, samplesPerBlock, coef1, coef2),
            _ => null,
        };
        if (samples == null || samples.Length < channels) return null;

        AudioClip clip = AudioClip.Create(name, samples, channels, rate);
        if (clip != null) clip.ClipName = name;
        return clip;
    }

    private static float[] Pcm16(byte[] data, int at, int length)
    {
        var samples = new float[length / 2];
        for (int i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(data, at + i * 2) / 32768f;
        return samples;
    }

    private static float[] Pcm8(byte[] data, int at, int length)
    {
        var samples = new float[length];
        for (int i = 0; i < samples.Length; i++) samples[i] = (data[at + i] - 128) / 128f;
        return samples;
    }

    private static float[] MsAdpcm(byte[] data, int at, int length, int channels, int blockAlign, int samplesPerBlock,
        short[] coef1, short[] coef2)
    {
        int headerSize = 7 * channels;
        if (blockAlign <= headerSize) return null;
        if (samplesPerBlock <= 0) samplesPerBlock = (blockAlign - headerSize) * 2 / channels + 2;

        int blocks = (length + blockAlign - 1) / blockAlign;
        var output = new List<float>(blocks * samplesPerBlock * channels);
        Span<int> predictor = stackalloc int[2], delta = stackalloc int[2], s1 = stackalloc int[2], s2 = stackalloc int[2];

        for (int b = 0; b < blocks; b++)
        {
            int block = at + b * blockAlign;
            int end = Math.Min(block + blockAlign, at + length);
            if (end - block < headerSize) break;

            int p = block;
            for (int c = 0; c < channels; c++) predictor[c] = Math.Clamp((int)data[p++], 0, coef1.Length - 1);
            for (int c = 0; c < channels; c++, p += 2) delta[c] = BitConverter.ToInt16(data, p);
            for (int c = 0; c < channels; c++, p += 2) s1[c] = BitConverter.ToInt16(data, p);
            for (int c = 0; c < channels; c++, p += 2) s2[c] = BitConverter.ToInt16(data, p);

            for (int c = 0; c < channels; c++) output.Add(s2[c] / 32768f);
            for (int c = 0; c < channels; c++) output.Add(s1[c] / 32768f);

            int written = 2;
            int channel = 0;
            for (; p < end && written < samplesPerBlock; p++)
            {
                for (int half = 0; half < 2; half++)
                {
                    int nibble = half == 0 ? data[p] >> 4 : data[p] & 15;
                    int c = channel;
                    int signed = nibble >= 8 ? nibble - 16 : nibble;
                    int predict = (s1[c] * coef1[predictor[c]] + s2[c] * coef2[predictor[c]]) >> 8;
                    int sample = Math.Clamp(predict + signed * delta[c], short.MinValue, short.MaxValue);
                    s2[c] = s1[c];
                    s1[c] = sample;
                    delta[c] = Math.Max(16, s_adaptation[nibble] * delta[c] >> 8);
                    output.Add(sample / 32768f);

                    channel = (channel + 1) % channels;
                    if (channel == 0) written++;
                }
            }
        }

        return output.ToArray();
    }
}

/// <summary>A sound script entry: the files it picks from, and its volume and pitch ranges (pitch as a rate).</summary>
internal sealed class SoundEntry
{
    public List<string> Waves = new();
    public float MinVolume = 1f, MaxVolume = 1f, MinPitch = 1f, MaxPitch = 1f;
}

/// <summary>
/// A game's sound scripts (scripts/game_sounds_manifest.txt) and soundscapes (scripts/soundscapes_manifest.txt),
/// read when first asked for.
/// </summary>
internal sealed class SoundScripts
{
    private readonly IContent _content;
    private Dictionary<string, SoundEntry> _entries;
    private Dictionary<string, KeyValues> _soundscapes;

    public SoundScripts(IContent content) => _content = content;

    public SoundEntry Find(string name)
    {
        _entries ??= LoadEntries();
        return _entries.GetValueOrDefault(name.Trim());
    }

    public KeyValues Soundscape(string name)
    {
        _soundscapes ??= LoadSoundscapes();
        return _soundscapes.GetValueOrDefault(name.Trim());
    }

    private Dictionary<string, SoundEntry> LoadEntries()
    {
        var entries = new Dictionary<string, SoundEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ManifestFiles("scripts/game_sounds_manifest.txt", "scripts/game_sounds.txt"))
        {
            List<KeyValues> all = KeyValues.ParseAll(_content.Read(file));
            if (all == null) continue;
            foreach (KeyValues entry in all)
            {
                if (!entry.IsBlock) continue;
                var sound = new SoundEntry();
                (sound.MinVolume, sound.MaxVolume) = Range(entry.Get("volume"), 1f, Volume);
                (sound.MinPitch, sound.MaxPitch) = Range(entry.Get("pitch"), 100f, Pitch);
                sound.MinPitch /= 100f;
                sound.MaxPitch /= 100f;
                if (entry.Get("wave") is { } wave) sound.Waves.Add(wave);
                if (entry["rndwave"] is { } random)
                    foreach (KeyValues w in random.All("wave")) if (w.Value != null) sound.Waves.Add(w.Value);
                if (sound.Waves.Count > 0) entries.TryAdd(entry.Key, sound);
            }
        }
        return entries;
    }

    private Dictionary<string, KeyValues> LoadSoundscapes()
    {
        var soundscapes = new Dictionary<string, KeyValues>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in ManifestFiles("scripts/soundscapes_manifest.txt", "scripts/soundscapes.txt"))
            AddSoundscapes(soundscapes, _content.Read(file));
        return soundscapes;
    }

    public static void AddSoundscapes(Dictionary<string, KeyValues> into, byte[] file)
    {
        List<KeyValues> all = KeyValues.ParseAll(file);
        if (all == null) return;
        foreach (KeyValues soundscape in all)
            if (soundscape.IsBlock) into.TryAdd(soundscape.Key, soundscape);
    }

    /// <summary>The files a manifest lists, or the fallback when there is no manifest.</summary>
    private IEnumerable<string> ManifestFiles(string manifest, string fallback)
    {
        KeyValues root = KeyValues.Parse(_content.Read(manifest));
        if (root == null)
        {
            yield return fallback;
            yield break;
        }
        foreach (KeyValues child in root.Children)
            if (child.Value != null) yield return child.Value;
    }

    private static float Volume(string word) => word.ToUpperInvariant() switch
    {
        "VOL_NORM" => 1f,
        _ => float.NaN,
    };

    private static float Pitch(string word) => word.ToUpperInvariant() switch
    {
        "PITCH_NORM" => 100f,
        "PITCH_LOW" => 95f,
        "PITCH_HIGH" => 120f,
        _ => float.NaN,
    };

    /// <summary>"0.5", "0.4, 0.6" or a named constant, as a range.</summary>
    public static (float Min, float Max) Range(string value, float fallback, Func<string, float> named = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return (fallback, fallback);
        string[] parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        float Parse(string text)
        {
            if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) return v;
            float n = named?.Invoke(text) ?? float.NaN;
            return float.IsNaN(n) ? fallback : n;
        }
        float min = Parse(parts[0]);
        float max = parts.Length > 1 ? Parse(parts[1]) : min;
        return (Math.Min(min, max), Math.Max(min, max));
    }
}

/// <summary>
/// A Source map's sounds: files and sound scripts from the map's own pak and the mounted games, and their
/// soundscapes. The clips it makes belong to the map.
/// </summary>
internal sealed class SourceMapAudio : IMapAudio, IDisposable
{
    private readonly IContent _content;
    private readonly List<SoundScripts> _scripts;
    private readonly Dictionary<string, KeyValues> _mapSoundscapes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AudioClip> _clips = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Soundscape> _built = new(StringComparer.OrdinalIgnoreCase);

    public SourceMapAudio(IContent content, List<SoundScripts> scripts, byte[] mapSoundscapes)
    {
        _content = content;
        _scripts = scripts;
        SoundScripts.AddSoundscapes(_mapSoundscapes, mapSoundscapes);
    }

    public MapSoundClip? Find(string sound)
    {
        if (string.IsNullOrWhiteSpace(sound)) return null;
        string clean = SourceAudio.CleanName(sound);
        if (clean.EndsWith(".wav") || clean.EndsWith(".mp3") || clean.EndsWith(".ogg"))
            return Clip(clean) is { } file ? new MapSoundClip(file, 1f, 1f) : null;

        foreach (SoundScripts scripts in _scripts)
        {
            SoundEntry entry = scripts.Find(sound);
            if (entry == null) continue;
            string wave = entry.Waves[Random.Shared.Next(entry.Waves.Count)];
            AudioClip clip = Clip(SourceAudio.CleanName(wave));
            if (clip == null) return null;
            return new MapSoundClip(clip, Between(entry.MinVolume, entry.MaxVolume), Between(entry.MinPitch, entry.MaxPitch));
        }
        return null;
    }

    private static float Between(float min, float max) => min + (max - min) * Random.Shared.NextSingle();

    private AudioClip Clip(string path)
    {
        if (_clips.TryGetValue(path, out AudioClip cached)) return cached.IsValid() ? cached : null;
        AudioClip clip = null;
        try
        {
            clip = SourceAudio.Clip(path, _content.Read("sound/" + path));
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Could not load sound {path}: {e.Message}");
        }
        _clips[path] = clip;
        return clip;
    }

    public Soundscape Soundscape(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_built.TryGetValue(name, out Soundscape cached)) return cached;

        Soundscape soundscape = null;
        if (FindSoundscape(name) != null)
        {
            soundscape = new Soundscape { Name = name };
            Fold(soundscape, name, 1f, 0);
        }
        _built[name] = soundscape;
        return soundscape;
    }

    private KeyValues FindSoundscape(string name)
    {
        if (_mapSoundscapes.TryGetValue(name, out KeyValues own)) return own;
        foreach (SoundScripts scripts in _scripts)
            if (scripts.Soundscape(name) is { } found) return found;
        return null;
    }

    /// <summary>Adds a soundscape's sounds to <paramref name="into"/>, and those of the soundscapes it plays.</summary>
    private void Fold(Soundscape into, string name, float volume, int depth)
    {
        KeyValues definition = FindSoundscape(name);
        if (definition == null || depth > 6) return;

        foreach (KeyValues rule in definition.Children)
        {
            if (!rule.IsBlock) continue;
            (float minVolume, float maxVolume) = SoundScripts.Range(rule.Get("volume"), 1f);
            (float minPitch, float maxPitch) = SoundScripts.Range(rule.Get("pitch"), 100f);

            switch (rule.Key.ToLowerInvariant())
            {
                case "playlooping":
                    if (rule.Get("wave") is { } wave)
                        into.Loops.Add(new Soundscape.Loop(wave, (minVolume + maxVolume) * 0.5f * volume, (minPitch + maxPitch) / 200f));
                    break;
                case "playrandom":
                {
                    var waves = new List<string>();
                    if (rule.Get("wave") is { } single) waves.Add(single);
                    if (rule["rndwave"] is { } random)
                        foreach (KeyValues w in random.All("wave")) if (w.Value != null) waves.Add(w.Value);
                    if (waves.Count == 0) break;
                    (float minTime, float maxTime) = SoundScripts.Range(rule.Get("time"), 10f);
                    into.Randoms.Add(new Soundscape.Random(waves, Math.Max(0.1f, minTime), Math.Max(0.1f, maxTime),
                        minVolume * volume, maxVolume * volume, minPitch / 100f, maxPitch / 100f));
                    break;
                }
                case "playsoundscape":
                    if (rule.Get("name") is { } inner) Fold(into, inner, volume * (minVolume + maxVolume) * 0.5f, depth + 1);
                    break;
            }
        }
    }

    public void Dispose()
    {
        foreach (AudioClip clip in _clips.Values)
            if (clip.IsValid()) clip.Dispose();
        _clips.Clear();
    }
}
