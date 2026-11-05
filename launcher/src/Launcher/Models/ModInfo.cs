using System.IO;
using System.Linq;

namespace LeafItAlone.Launcher.Models;

/// <summary>
/// A discovered mod: its parsed <see cref="Manifest"/> plus the absolute folder it
/// was found in. Wraps a few convenience paths the apply/revert orchestration
/// (L3) and config editor (L2) will need. Read-only for L1.
/// </summary>
public sealed class ModInfo
{
    public ModInfo(ModManifest manifest, string folder, string manifestPath)
    {
        Manifest = manifest;
        Folder = folder;
        ManifestPath = manifestPath;
    }

    public ModManifest Manifest { get; }

    /// <summary>Absolute <c>content-mods/&lt;id&gt;</c> directory.</summary>
    public string Folder { get; }

    /// <summary>Absolute path to the <c>mod.json</c> this was built from.</summary>
    public string ManifestPath { get; }

    /// <summary>Convenience for the list UI.</summary>
    public string Id => Manifest.Id;
    public string Name => Manifest.Name;
    public string Version => Manifest.Version;
    public string Description => Manifest.Description;
    public int Order => Manifest.Order;

    /// <summary>Absolute apply script, resolved against the mod folder.</summary>
    public string ApplyScript => Path.Combine(Folder, Manifest.Apply.Script);

    /// <summary>Absolute revert script, resolved against the mod folder.</summary>
    public string RevertScript => Path.Combine(Folder, Manifest.Revert.Script);

    /// <summary>Human-readable list of the preconditions the launcher will assert (L3).</summary>
    public string RequiresSummary
    {
        get
        {
            var r = Manifest.Requires;
            var on = new System.Collections.Generic.List<string>();
            if (r.SafeBase) on.Add("safe-base installed");
            if (r.GameNotRunning) on.Add("game closed");
            if (r.Python) on.Add("bundled Python");
            return on.Count == 0 ? "none" : string.Join(", ", on);
        }
    }

    public override string ToString() => $"{Name} ({Id}) v{Version}";
}
