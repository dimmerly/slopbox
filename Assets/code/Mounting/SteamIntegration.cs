using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Prowl.Runtime;

namespace Slopbox.Mounting;

/// <summary>What mounts may ask about the Steam library. Ported from s&amp;box's ISteamIntegration.</summary>
public interface ISteamIntegration
{
    bool IsAppInstalled(long appId);
    bool IsDlcInstalled(long appId);
    string GetAppDirectory(long appId);
}

/// <summary>
/// Finds installed Steam apps from Steam's own files, without the Steam client running: the library folders
/// in libraryfolders.vdf and each app's appmanifest_{id}.acf. s&amp;box asks Steamworks instead.
/// </summary>
internal sealed class SteamIntegration : ISteamIntegration
{
    private List<string> _libraries;
    private readonly Dictionary<long, string> _appDirs = new();

    public bool IsAppInstalled(long appId) => GetAppDirectory(appId) != null;

    // DLC installs into its game's folder, with nothing on disk saying it is there.
    public bool IsDlcInstalled(long appId) => false;

    public string GetAppDirectory(long appId)
    {
        if (_appDirs.TryGetValue(appId, out string cached)) return cached;

        string found = null;
        foreach (string library in Libraries)
        {
            string manifest = Path.Combine(library, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;

            try
            {
                string text = File.ReadAllText(manifest);
                string installDir = Value(text, "installdir");
                if (string.IsNullOrEmpty(installDir)) continue;

                // StateFlags 4 is fully installed; anything else is downloading, updating or broken.
                if (int.TryParse(Value(text, "StateFlags"), out int state) && (state & 4) == 0) continue;

                string dir = Path.Combine(library, "steamapps", "common", installDir);
                if (Directory.Exists(dir))
                {
                    found = dir;
                    break;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Could not read {manifest}: {e.Message}");
            }
        }

        _appDirs[appId] = found;
        return found;
    }

    private List<string> Libraries => _libraries ??= FindLibraries();

    private static List<string> FindLibraries()
    {
        var libraries = new List<string>();
        string steam = FindSteam();
        if (steam == null) return libraries;

        libraries.Add(steam);
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return libraries;

        try
        {
            foreach (Match match in PathEntry().Matches(File.ReadAllText(vdf)))
            {
                string path = Unescape(match.Groups[1].Value);
                if (Directory.Exists(path) && !libraries.Exists(l => SamePath(l, path))) libraries.Add(path);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"Could not read {vdf}: {e.Message}");
        }

        return libraries;
    }

    private static string FindSteam()
    {
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string path) candidates.Add(path);
            }
            catch
            {
                // No registry access. The default folders below still get a look.
            }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        }
        else
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, ".steam", "steam"));
            candidates.Add(Path.Combine(home, ".local", "share", "Steam"));
            candidates.Add(Path.Combine(home, "Library", "Application Support", "Steam"));
        }

        foreach (string candidate in candidates)
            if (Directory.Exists(Path.Combine(candidate, "steamapps"))) return Path.GetFullPath(candidate);
        return null;
    }

    // A quoted value after a quoted key in Valve's KeyValues text, such as "installdir" "Half-Life".
    private static string Value(string text, string key)
    {
        Match match = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase);
        return match.Success ? Unescape(match.Groups[1].Value) : null;
    }

    private static string Unescape(string value) => value.Replace("\\\\", "\\");

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static readonly Regex s_pathEntry = new("\"path\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase);

    private static Regex PathEntry() => s_pathEntry;
}
