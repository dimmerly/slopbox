using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Prowl.Runtime;
using Prowl.Runtime.Resources;

namespace Slopbox;

/// <summary>
/// Every material under a Resources folder (by default <c>Assets/Resources/materials</c>), for the material
/// tool. Entries are listed without loading anything; each material loads in the background the first time
/// it is asked for and stays loaded after.
/// </summary>
public sealed class MaterialCatalog
{
    public sealed class Entry
    {
        public Guid Guid { get; init; }
        public string Name { get; init; } = "";

        /// <summary>The loaded material, or null until it has loaded.</summary>
        public Material Material { get; internal set; }

        internal bool Loading;
    }

    private readonly List<Entry> _entries = new();

    public IReadOnlyList<Entry> All => _entries;

    public MaterialCatalog(string folder = "materials")
    {
        string prefix = folder.Trim('/') + "/";
        foreach (AssetBackend backend in AssetDatabase.Mounts.Append(AssetDatabase.Backend))
        {
            if (backend == null) continue;
            foreach (ResourceEntry entry in backend.Resources)
            {
                if (entry.LoadPath.Contains('#')) continue;
                if (!entry.LoadPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.LoadPath.IndexOf('/', prefix.Length) >= 0) continue;   // not the textures sub folder
                if (AssetDatabase.GetAssetType(entry.Guid) != typeof(Material)) continue;

                _entries.Add(new Entry { Guid = entry.Guid, Name = DisplayName(entry.LoadPath[prefix.Length..]) });
            }
        }

        _entries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The entry's material, starting its load if needed. Null until it has loaded.</summary>
    public Material Get(Entry entry)
    {
        if (entry.Material.IsNotValid() && !entry.Loading) Load(entry);
        return entry.Material;
    }

    private static async void Load(Entry entry)
    {
        entry.Loading = true;
        try
        {
            Material material = await AssetDatabase.LoadAsync<Material>(entry.Guid);

            // The picker draws the albedo itself, outside the material, so it has to be kept loaded too.
            Texture2D albedo = material.IsValid() ? material._properties.GetTexture("_MainTex") : null;
            if (albedo.IsValid()) AssetDatabase.Hold(albedo, entry);
            entry.Material = material;
        }
        catch (Exception e)
        {
            Debug.LogException(e);
        }
    }

    public Entry Find(Material material)
    {
        if (material.IsNotValid()) return null;
        foreach (Entry e in _entries)
            if (e.Guid == material.AssetID) return e;
        return null;
    }

    /// <summary>"brick_wall_tx_1" becomes "Brick Wall Tx 1". Numbers are kept, as they tell variants apart.</summary>
    private static string DisplayName(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        bool upper = true;
        foreach (char ch in raw)
        {
            if (ch is '_' or '-')
            {
                sb.Append(' ');
                upper = true;
                continue;
            }
            sb.Append(upper ? char.ToUpperInvariant(ch) : ch);
            upper = false;
        }
        return sb.ToString();
    }
}
