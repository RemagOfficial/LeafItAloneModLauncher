using System;
using System.Collections.Generic;
using System.IO;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// Finds the "app root" — the folder that holds the mod content and the engines
/// the launcher shells out to. Two supported layouts:
///
///  * <b>Repo / dev</b>: run from <c>launcher/src/Launcher/bin/...</c>; we walk up
///    until we find the git repo root, which contains <c>content-mods/</c> and
///    <c>safe-base/</c>.
///  * <b>Published</b>: <c>Launcher.exe</c> sits beside <c>content-mods/</c> and the
///    engine scripts under <c>tools/</c> (a copy of safe-base). BaseDirectory then
///    already satisfies the marker check.
///
/// For L1 discovery only the <see cref="ModsRoot"/> matters; the engine dir is
/// wired up in L3+. A null result is reported via <see cref="LastError"/> so the
/// UI can tell the user what to fix instead of crashing on a bare NRE.
/// </summary>
public sealed class RepoLocator
{
    /// <summary>A directory that marks the app root alongside <c>content-mods</c>.</summary>
    private static readonly string[] EngineMarkers = { "safe-base", "tools" };

    public string? Root { get; private set; }
    public string? ModsRoot { get; private set; }
    public string? EngineRoot { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>
    /// Resolve the app root from <paramref name="start"/> (default: the executable
    /// folder) walking upward. Returns true when a root with a <c>content-mods</c>
    /// directory and an engine marker is found.
    /// </summary>
    public bool TryLocate(string? start = null)
    {
        Root = ModsRoot = EngineRoot = null;
        LastError = null;

        var dir = new DirectoryInfo(start ?? AppContext.BaseDirectory);
        var probes = new List<string>();

        while (dir is not null)
        {
            probes.Add(dir.FullName);
            var mods = Path.Combine(dir.FullName, "content-mods");
            if (Directory.Exists(mods))
            {
                foreach (var marker in EngineMarkers)
                {
                    var engine = Path.Combine(dir.FullName, marker);
                    if (Directory.Exists(engine))
                    {
                        Root = dir.FullName;
                        ModsRoot = mods;
                        EngineRoot = engine;
                        return true;
                    }
                }
            }
            dir = dir.Parent;
        }

        LastError =
            "Could not locate the launcher content root. Expected a 'content-mods' " +
            "folder beside a 'safe-base' (dev) or 'tools' (published) folder. Searched:\n  " +
            string.Join("\n  ", probes);
        return false;
    }

    /// <summary>The safe-base (dev) or tools (published) folder holding the engine scripts.</summary>
    public string SafeBaseDir =>
        EngineRoot ?? throw new InvalidOperationException("RepoLocator.TryLocate must succeed before use.");
}
