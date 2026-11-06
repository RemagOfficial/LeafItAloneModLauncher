using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// 2b: builds a support bundle - a zip of the launcher's own diagnostics - with one
/// hard rule: <b>no game-derived content ever leaves the machine.</b> The safety tree
/// (<c>backup/</c>, <c>mod-backups/</c>) and every engine asset we patch
/// (<c>*.bundle</c>, <c>catalog.bin</c>, Unity resource extensions) are refused by the
/// deny-list, mirroring the repo's own <c>.gitignore</c> "never commit game files" rule.
/// Only settings, state, per-mod configs, remote mod <c>mod.json</c> manifests and a
/// generated <c>about.txt</c> go in. Uses the in-box <see cref="ZipFile"/> so there is
/// still no third-party dependency and the project stays offline-buildable.
/// </summary>
public static class DiagnosticsExporter
{
    /// <summary>Whole subtrees never included (relative segment match, case-insensitive).</summary>
    private static readonly string[] DeniedDirs = { "backup", "mod-backups" };

    /// <summary>Extensions / names that are game-derived and must never be bundled.</summary>
    private static readonly string[] DeniedExt = { ".bundle", ".assets", ".resS", ".unity", ".unityfs", ".cabs" };
    private static readonly string[] DeniedNames = { "catalog.bin" };

    public static string OutputDir(string safeRoot) => Path.Combine(safeRoot, "diagnostics");

    /// <summary>True when a file path must be excluded (backup tree or a game asset).</summary>
    public static bool IsDenied(string fullPath)
    {
        var name = Path.GetFileName(fullPath);
        if (DeniedNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;
        var ext = Path.GetExtension(fullPath);
        if (!string.IsNullOrEmpty(ext) && DeniedExt.Contains(ext, StringComparer.OrdinalIgnoreCase)) return true;
        foreach (var seg in fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            if (DeniedDirs.Contains(seg, StringComparer.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// Write <c>diag-&lt;timestamp&gt;.zip</c> under <paramref name="safeRoot"/>/diagnostics and
    /// return its path. <paramref name="aboutText"/> becomes <c>about.txt</c>. Missing inputs are
    /// skipped silently; a denied file is never added even if a caller lists it explicitly.
    /// </summary>
    public static string Export(string safeRoot, string aboutText)
    {
        var outDir = OutputDir(safeRoot);
        Directory.CreateDirectory(outDir);
        var zipPath = Path.Combine(outDir, $"diag-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);

        void Add(string absPath, string entryName)
        {
            if (IsDenied(absPath)) return;                 // the deny-list is the last line of defence
            if (!File.Exists(absPath)) return;
            try { zip.CreateEntryFromFile(absPath, entryName); } catch { /* a locked file is not worth failing the bundle */ }
        }

        // 1. launcher/ settings, state, sources, crash log.
        var launcherDir = Path.Combine(safeRoot, "launcher");
        foreach (var f in SafeEnumerate(launcherDir, "*.json", SearchOption.AllDirectories))
            Add(f, "launcher/" + Relative(f, launcherDir));
        foreach (var f in SafeEnumerate(launcherDir, "*.log", SearchOption.AllDirectories))
            Add(f, "launcher/" + Relative(f, launcherDir));

        // 2. the live per-mod configs sitting directly at the LeafItAloneSafe root
        //    (bag-capacity.json etc.); these are what a "reset to defaults" fixes, so
        //    support needs to see them. Non-recursive so we don't drag in unrelated dirs.
        foreach (var f in SafeEnumerate(safeRoot, "*.json", SearchOption.TopDirectoryOnly))
            Add(f, "configs/" + Path.GetFileName(f));

        // 3. remote-mods provenance: ONLY mod.json manifests (never their scripts/bundles).
        var remoteDir = Path.Combine(safeRoot, "remote-mods");
        foreach (var f in SafeEnumerate(remoteDir, "mod.json", SearchOption.AllDirectories))
            Add(f, "remote/" + Relative(f, remoteDir));

        // 4. generated about.txt.
        try
        {
            var entry = zip.CreateEntry("about.txt");
            using var w = new StreamWriter(entry.Open());
            w.Write(aboutText ?? "");
        }
        catch { /* ignore */ }

        return zipPath;
    }

    private static IEnumerable<string> SafeEnumerate(string dir, string pattern, SearchOption opt)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, pattern, opt).Where(p => !IsDenied(p)).ToList()
                : Enumerable.Empty<string>();
        }
        catch { return Enumerable.Empty<string>(); }
    }

    private static string Relative(string full, string root)
    {
        var len = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length + 1;
        return full.Length > len ? full[len..].Replace('\\', '/') : Path.GetFileName(full);
    }
}
