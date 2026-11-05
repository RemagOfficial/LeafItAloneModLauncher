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
            return JsonSerializer.Deserialize<LauncherState>(text) ?? new LauncherState();
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
