using System.Collections.Generic;
using Prowl.Runtime;

namespace Slopbox;

public enum NotifyKind { Info, Undo, Error }

/// <summary>
/// The feed of short messages in the bottom right ("Undone Prop", "Saved dupe"). Anything can
/// post to it; the HUD draws and expires them.
/// </summary>
public static class Notify
{
    public sealed class Entry
    {
        public string Text = "";
        public NotifyKind Kind;
        public double PostedAt;
        public int Id;
    }

    public const float Lifetime = 4f;
    public const int MaxVisible = 6;

    private static readonly List<Entry> s_entries = new();
    private static int s_nextId;

    public static IReadOnlyList<Entry> Entries => s_entries;

    public static void Info(string text) => Post(text, NotifyKind.Info);
    public static void Undo(string text) => Post(text, NotifyKind.Undo);
    public static void Error(string text) => Post(text, NotifyKind.Error);

    public static void Post(string text, NotifyKind kind)
    {
        GameManager game = GameManager.Instance;
        if (game.IsValid() && game.ActingPlayer.IsValid() && !game.ActingPlayer.IsLocalPlayer)
        {
            game.ActingPlayer.ReceiveNotice(text, kind);
            return;
        }
        if (kind == NotifyKind.Error) GameAudio.Error();
        else if (kind == NotifyKind.Undo) GameAudio.Play("undo", 0.35f, ui: true);
        else GameAudio.Play("confirm", 0.2f, ui: true, cooldown: 0.15f);
        s_entries.Add(new Entry { Text = text, Kind = kind, PostedAt = Time.TimeSinceStartup, Id = s_nextId++ });
        while (s_entries.Count > MaxVisible) s_entries.RemoveAt(0);
    }

    /// <summary>Drops expired entries. Called by the HUD once a frame.</summary>
    public static void Expire()
    {
        double now = Time.TimeSinceStartup;
        s_entries.RemoveAll(e => now - e.PostedAt > Lifetime);
    }
}
