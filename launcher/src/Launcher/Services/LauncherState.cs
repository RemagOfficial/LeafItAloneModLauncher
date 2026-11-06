using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeafItAlone.Launcher.Services;

/// <summary>Per-mod launcher bookkeeping (distinct from the mod's own config file).</summary>
public sealed class ModState
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    /// <summary>SHA of the live config the last time this mod was applied (L3 records it).</summary>
    [JsonPropertyName("lastAppliedConfigSha")] public string LastAppliedConfigSha { get; set; } = "";

    /// <summary>Mod version recorded at last successful apply.</summary>
    [JsonPropertyName("appliedVersion")] public string AppliedVersion { get; set; } = "";

    /// <summary>
    /// Mod-sources (2a R3): provenance + self-healing manifest for a mod installed
    /// from a remote source. Null (or missing JSON key) for built-in mods shipped in
    /// <c>content-mods/</c>. When present, Apply must call
    /// <see cref="RemoteModStager.EnsurePresentAsync"/> first so any files the user
    /// deleted by hand are re-downloaded from <see cref="RemoteState.Sha"/>.
    /// </summary>
    [JsonPropertyName("remote")] public RemoteState? Remote { get; set; }
}

/// <summary>
/// Where a remote mod came from and what files it is expected to contain. Recorded
/// on Install, updated on Update, and verified on every Apply. Never trusted over
/// the source listing when they disagree: <see cref="RemoteModStager"/> re-fetches
/// the full file manifest from the pinned commit SHA before running scripts.
/// </summary>
public sealed class RemoteState
{
    /// <summary>Source key (from <see cref="ModSource.Key"/>) this mod was installed from.</summary>
    [JsonPropertyName("sourceKey")] public string SourceKey { get; set; } = "";

    /// <summary>Repo URL snapshot at install time, so we still work if the user removes the source.</summary>
    [JsonPropertyName("sourceUrl")] public string SourceUrl { get; set; } = "";

    /// <summary>Pinned commit SHA. Never changes across an "Update" without the state being rewritten.</summary>
    [JsonPropertyName("sha")] public string Sha { get; set; } = "";

    /// <summary>ISO-8601 UTC stamp of the last successful install/update.</summary>
    [JsonPropertyName("installedUtc")] public string InstalledUtc { get; set; } = "";

    /// <summary>
    /// Path inside the repo where this mod lives, e.g. <c>content-mods/my-mod</c>.
    /// Kept so healing does not have to re-list the whole source (which would
    /// re-download every mod's file tree just to find one path) and so we support
    /// a future layout that is not exactly <c>content-mods/&lt;id&gt;</c>.
    /// </summary>
    [JsonPropertyName("folderPathInRepo")] public string FolderPathInRepo { get; set; } = "";

    /// <summary>
    /// Per-file git blob SHA the launcher wrote at install time, keyed by relative
    /// path (from the mod folder root). <see cref="RemoteModStager.EnsurePresentAsync"/>
    /// compares on-disk files against these and re-downloads only the mismatched/missing
    /// ones, so a hand-deleted file costs exactly one raw download to heal.
    /// </summary>
    [JsonPropertyName("filesManifest")]
    public Dictionary<string, string> FilesManifest { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// <c>%LOCALAPPDATA%\LeafItAloneSafe\launcher\state.json</c>: the launcher's own
/// state, keyed by mod id (plan "New launcher/state.json"). It never touches the
/// mod config values — it only remembers what's enabled and which config hash was
/// last applied, so "pending" = enabled-state changed OR live-config sha !=
/// lastAppliedConfigSha. Apply (L3/L4) writes the sha; L2 only reads it to drive
/// the badge.
/// </summary>
public sealed class LauncherState
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [JsonPropertyName("mods")] public Dictionary<string, ModState> Mods { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Get-or-create the state entry for a mod id.</summary>
    public ModState For(string id)
    {
        if (!Mods.TryGetValue(id, out var s))
        {
            s = new ModState();
            Mods[id] = s;
        }
        return s;
    }

    public static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "LeafItAloneSafe", "launcher", "state.json");
    }

    public static LauncherState Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new LauncherState();
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new LauncherState();
            var st = JsonSerializer.Deserialize<LauncherState>(text) ?? new LauncherState();
            // System.Text.Json materialises Dictionary<K,V> with the default
            // (ordinal, case-sensitive) comparer, silently dropping the
            // OrdinalIgnoreCase we set at the property-initialiser level. Re-wrap
            // both dictionaries so lookups stay case-insensitive across a save/
            // load round trip — Windows paths and mod ids drift in casing when
            // users edit state.json by hand.
            st.Mods = new Dictionary<string, ModState>(st.Mods, StringComparer.OrdinalIgnoreCase);
            foreach (var ms in st.Mods.Values)
            {
                if (ms.Remote is not null)
                    ms.Remote.FilesManifest = new Dictionary<string, string>(ms.Remote.FilesManifest, StringComparer.OrdinalIgnoreCase);
            }
            return st;
        }
        catch (Exception)
        {
            // Corrupt state must never brick the app; start fresh (the mod config is
            // the source of truth, this is only cache).
            return new LauncherState();
        }
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions));
    }
}
