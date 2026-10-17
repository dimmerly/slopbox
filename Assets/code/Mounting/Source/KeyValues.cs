using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Slopbox.Mounting.Source;

/// <summary>
/// Valve's KeyValues text, as materials (.vmt), sound scripts and soundscapes are written: quoted or bare keys,
/// each with a value or a { } block of more. Keys may repeat, so children are a list.
/// </summary>
internal sealed class KeyValues
{
    public string Key { get; }
    public string Value { get; }
    public List<KeyValues> Children { get; } = new();

    public bool IsBlock => Value == null;

    public KeyValues(string key, string value = null)
    {
        Key = key;
        Value = value;
    }

    /// <summary>The first child with <paramref name="key"/>, ignoring case, or null.</summary>
    public KeyValues this[string key]
    {
        get
        {
            foreach (KeyValues child in Children)
                if (child.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return child;
            return null;
        }
    }

    public string Get(string key, string fallback = null) => this[key]?.Value ?? fallback;

    public float GetFloat(string key, float fallback = 0f)
        => float.TryParse(Get(key)?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : fallback;

    public IEnumerable<KeyValues> All(string key)
    {
        foreach (KeyValues child in Children)
            if (child.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) yield return child;
    }

    /// <summary>Every top level entry of a file. Null if the bytes are empty.</summary>
    public static List<KeyValues> ParseAll(byte[] data)
    {
        if (data == null || data.Length == 0) return null;
        string text = Encoding.UTF8.GetString(data);
        int pos = 0;
        var result = new List<KeyValues>();
        ParseBlock(text, ref pos, result, topLevel: true);
        return result;
    }

    /// <summary>The first top level entry of a file, such as a material's shader block.</summary>
    public static KeyValues Parse(byte[] data)
    {
        List<KeyValues> all = ParseAll(data);
        return all is { Count: > 0 } ? all[0] : null;
    }

    private static void ParseBlock(string text, ref int pos, List<KeyValues> into, bool topLevel)
    {
        while (true)
        {
            SkipSpace(text, ref pos);
            if (pos >= text.Length) return;
            if (text[pos] == '}')
            {
                pos++;
                if (!topLevel) return;
                continue;
            }

            string key = ReadToken(text, ref pos);
            if (key == null) return;

            SkipSpace(text, ref pos);
            // A conditional such as [$WIN32] may sit between a key and its value.
            if (pos < text.Length && text[pos] == '[') SkipConditional(text, ref pos);
            if (pos >= text.Length) return;

            if (text[pos] == '{')
            {
                pos++;
                var block = new KeyValues(key);
                ParseBlock(text, ref pos, block.Children, topLevel: false);
                into.Add(block);
            }
            else
            {
                string value = ReadToken(text, ref pos);
                if (value == null) return;
                SkipSpaceOnLine(text, ref pos);
                if (pos < text.Length && text[pos] == '[') SkipConditional(text, ref pos);
                into.Add(new KeyValues(key, value));
            }
        }
    }

    private static void SkipConditional(string text, ref int pos)
    {
        while (pos < text.Length && text[pos] != ']') pos++;
        if (pos < text.Length) pos++;
        SkipSpace(text, ref pos);
    }

    private static void SkipSpaceOnLine(string text, ref int pos)
    {
        while (pos < text.Length && text[pos] is ' ' or '\t') pos++;
    }

    private static void SkipSpace(string text, ref int pos)
    {
        while (pos < text.Length)
        {
            if (char.IsWhiteSpace(text[pos]) || text[pos] == '﻿')
            {
                pos++;
            }
            else if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
            }
            else
            {
                break;
            }
        }
    }

    private static string ReadToken(string text, ref int pos)
    {
        if (pos >= text.Length) return null;

        if (text[pos] == '"')
        {
            pos++;
            var sb = new StringBuilder();
            while (pos < text.Length && text[pos] != '"')
            {
                // Paths use backslashes, so only an escaped quote is unescaped.
                if (text[pos] == '\\' && pos + 1 < text.Length && text[pos + 1] == '"') pos++;
                sb.Append(text[pos++]);
            }
            if (pos < text.Length) pos++;
            return sb.ToString();
        }

        int start = pos;
        while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"'))
        {
            if (pos + 1 < text.Length && text[pos] == '/' && text[pos + 1] == '/') break;
            pos++;
        }
        return pos > start ? text[start..pos] : null;
    }
}
