using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Prowl.Runtime;

namespace Slopbox;

/// <summary>
/// Duplicator copies saved as <c>.dupe</c> files (JSON). In the editor they live in the project's own
/// <c>dupes/</c> folder; a built game uses <c>%AppData%/Slopbox/dupes</c>.
/// </summary>
public static class Dupes
{
    public const string Extension = ".dupe";

    public sealed record Entry(string Name, DateTime Modified, int Props);

#if PROWL_EDITOR
    public static string Folder => Path.Combine(Prowl.Editor.Projects.Project.Current.RootPath, "dupes");
#else
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Slopbox", "dupes");
#endif

    // Prop counts need the file parsed, so they are kept until the file changes.
    private static readonly Dictionary<string, (DateTime Modified, int Props)> s_counts = new();
    private static List<Entry> s_list;
    private static float s_listedAt = -100f;

    /// <summary>Newest first. Read from disk at most once a second, as the menu asks every frame.</summary>
    public static IReadOnlyList<Entry> List()
    {
        if (s_list != null && Time.UnscaledTotalTime - s_listedAt < 1f) return s_list;
        s_listedAt = Time.UnscaledTotalTime;
        s_list = new List<Entry>();
        if (!Directory.Exists(Folder)) return s_list;

        foreach (string path in Directory.GetFiles(Folder, "*" + Extension))
        {
            DateTime modified = File.GetLastWriteTime(path);
            if (!s_counts.TryGetValue(path, out var info) || info.Modified != modified)
            {
                int props;
                try { props = Snapshot.FromJson(File.ReadAllText(path))?.Count ?? -1; }
                catch (Exception) { props = -1; }
                s_counts[path] = info = (modified, props);
            }
            s_list.Add(new Entry(Path.GetFileNameWithoutExtension(path), modified, info.Props));
        }

        s_list.Sort((a, b) => b.Modified.CompareTo(a.Modified));
        return s_list;
    }

    /// <summary>A file safe version of <paramref name="name"/>, or a dated default when it is blank.</summary>
    public static string CleanName(string name)
    {
        char[] bad = Path.GetInvalidFileNameChars();
        string clean = new string(name.Where(c => !bad.Contains(c)).ToArray()).Trim();
        return clean.Length > 0 ? clean : $"Dupe {DateTime.Now:yyyy-MM-dd HH-mm-ss}";
    }

    public static bool Exists(string name) => File.Exists(PathOf(CleanName(name)));

    public static bool Save(string name, Snapshot dupe)
    {
        name = CleanName(name);
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(PathOf(name), dupe.ToJson());
            s_list = null;
            Notify.Info($"Saved {name} ({dupe.Count} props)");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Notify.Error($"Could not save {name}");
            return false;
        }
    }

    public static Snapshot Load(string name)
    {
        try
        {
            Snapshot dupe = Snapshot.FromJson(File.ReadAllText(PathOf(name)));
            if (dupe != null) return dupe;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
        Notify.Error($"Could not read {name}");
        return null;
    }

    public static void Delete(string name)
    {
        try
        {
            File.Delete(PathOf(name));
            s_list = null;
            Notify.Info($"Deleted {name}");
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Notify.Error($"Could not delete {name}");
        }
    }

    private static string PathOf(string name) => Path.Combine(Folder, name + Extension);
}
