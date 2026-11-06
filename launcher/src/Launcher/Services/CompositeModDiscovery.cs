using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LeafItAlone.Launcher.Models;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// Mod-sources (2a R3): discovers built-in mods from <c>content-mods/</c> and every
/// installed remote mod from <c>remote-mods/&lt;sourceKey&gt;/&lt;modId&gt;/</c>,
/// merging them into a single list keyed by <see cref="ModInfo.Id"/>. Built-ins win
/// on id collision so a user installing a remote mod that shadows a shipped one gets
/// a clear warning rather than a silent swap.
/// </summary>
public sealed class CompositeModDiscovery
{
    private readonly ModDiscovery _inner = new();

    /// <summary>
    /// Sweep both roots. <paramref name="remoteRoots"/> is an optional list of
    /// (sourceKey, absolute path) pairs; when null the discovery enumerates
    /// subfolders of <see cref="RepoLocator.RemoteModsRoot"/> automatically. Pass an
    /// explicit list in tests to avoid depending on the user's LocalAppData.
    /// </summary>
    public DiscoveryResult Discover(string builtInModsRoot, IEnumerable<(string SourceKey, string Url, string Path)>? remoteRoots = null)
    {
        var mods = new List<ModInfo>();
        var errors = new List<string>();

        // 1. Built-in mods: unchanged behaviour, but wrap ModInfo with IsRemote=false (default).
        var local = _inner.Discover(builtInModsRoot);
        errors.AddRange(local.Errors);
        foreach (var m in local.Mods) mods.Add(m);

        // 2. Remote mods: enumerate one level under remote-mods/<sourceKey>/<modId>/.
        var effective = remoteRoots ?? AutoEnumerateRemoteRoots();
        foreach (var (key, url, path) in effective)
        {
            if (!Directory.Exists(path)) continue;
            var result = _inner.Discover(path);
            foreach (var err in result.Errors) errors.Add(err + $" (source {key})");
            foreach (var raw in result.Mods)
            {
                // Rebuild ModInfo so the init-only source metadata is populated.
                var wrapped = new ModInfo(raw.Manifest, raw.Folder, raw.ManifestPath)
                {
                    SourceKey = key,
                    SourceUrl = url,
                    SourceSha = "",   // filled by RemoteModStager from LauncherState at Rescan time
                };
                mods.Add(wrapped);
            }
        }

        // 3. De-dup: built-in wins, remote is reported and skipped on collision.
        var byId = new Dictionary<string, ModInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in mods.OrderBy(x => x.Order).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (byId.TryGetValue(m.Id, out var existing))
            {
                if (existing.IsRemote && !m.IsRemote) byId[m.Id] = m;   // built-in over remote
                else if (!existing.IsRemote && m.IsRemote)
                    errors.Add($"remote mod '{m.Id}' (source {m.SourceKey}) skipped: same id is already shipped as a built-in");
                else
                    errors.Add($"duplicate remote mod '{m.Id}' from source {m.SourceKey} skipped");
                continue;
            }
            byId[m.Id] = m;
        }

        var sorted = byId.Values
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DiscoveryResult(sorted, errors);
    }

    private static IEnumerable<(string SourceKey, string Url, string Path)> AutoEnumerateRemoteRoots()
    {
        var root = RepoLocator.RemoteModsRoot;
        if (!Directory.Exists(root)) return Array.Empty<(string, string, string)>();

        // We cannot look up URLs here without a SourceRegistry dependency; pass an
        // empty url for auto-enumeration and let the caller (MainViewModel) supply
        // the richer tuple via the optional overload.
        return Directory.EnumerateDirectories(root).Select(dir =>
        {
            var key = Path.GetFileName(dir);
            return (key, "", dir);
        }).ToList();
    }
}
