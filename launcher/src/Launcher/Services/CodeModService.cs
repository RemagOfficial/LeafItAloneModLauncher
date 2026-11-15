using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LeafItAlone.Launcher.Models;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// Installs and removes CODE mods. Unlike a data mod (which patches the game bundle
/// through its apply/revert scripts), a code mod is hosted by the mod loader: it lives
/// as a folder under <c>&lt;GameDir&gt;\Mods\&lt;id&gt;\</c> containing its <c>mod.json</c>
/// and the managed DLL named there, and the loader discovers it at game start.
///
/// <para>
/// "Enabling" a code mod is therefore just installing that folder; "disabling" removes
/// it. Both mirror what the in-game Mods panel does, and both take effect on the next
/// launch (the loader scans <c>Mods\</c> once at boot). The whole mod folder is copied,
/// so any extra files a code mod ships (assets, a config JSON) travel with it. A running
/// game is refused because the loader keeps the hosted DLL open, so the copy would fail.
/// </para>
/// </summary>
public sealed class CodeModService
{
    /// <summary>The loader's mod-scan root inside the game folder.</summary>
    public static string ModsRoot(string gameDir) => Path.Combine(gameDir, "Mods");

    /// <summary>The folder one code mod is installed to: <c>&lt;GameDir&gt;\Mods\&lt;id&gt;\</c>.</summary>
    public static string InstallDir(string gameDir, ModInfo mod) => Path.Combine(ModsRoot(gameDir), mod.Id);

    /// <summary>
    /// Install <paramref name="mod"/> into <c>&lt;gameDir&gt;\Mods\&lt;id&gt;\</c> by copying its
    /// whole folder (manifest + DLL + any extras). Fails without touching the game when the
    /// manifest names a DLL that is not present. Idempotent: a re-copy overwrites in place.
    /// </summary>
    public Task<(bool Ok, string Message)> EnableAsync(
        ModInfo mod, string gameDir, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(mod.Manifest.Dll))
                return Task.FromResult((false, $"{mod.Id} declares no dll - it is not a code mod."));

            // Fail before writing anything if the DLL the loader must host is missing.
            var dllSource = Path.Combine(mod.Folder, mod.Manifest.Dll);
            if (!File.Exists(dllSource))
                return Task.FromResult((false, $"the code mod's dll '{mod.Manifest.Dll}' is not in its folder ({mod.Folder})."));

            var dest = InstallDir(gameDir, mod);
            Directory.CreateDirectory(dest);

            int copied = 0;
            foreach (var src in Directory.EnumerateFiles(mod.Folder, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                // Never carry staging/temp artifacts the downloader may have left behind.
                if (IsTransient(src)) continue;
                var rel = Path.GetRelativePath(mod.Folder, src);
                var dst = Path.Combine(dest, rel);
                var parent = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.Copy(src, dst, overwrite: true);
                copied++;
            }

            return Task.FromResult((true, $"installed code mod '{mod.Id}' to Mods\\{mod.Id} ({copied} file(s)); it loads next time you start the game."));
        }
        catch (IOException ex)
        {
            return Task.FromResult((false, $"could not install code mod '{mod.Id}': {ex.Message} (is the game running?)"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult((false, $"could not install code mod '{mod.Id}': {ex.Message}"));
        }
    }

    /// <summary>
    /// Remove <c>&lt;gameDir&gt;\Mods\&lt;id&gt;\</c>. Succeeds quietly when the folder is
    /// already gone (disable is idempotent); refuses only on a real I/O error (game running).
    /// </summary>
    public Task<(bool Ok, string Message)> DisableAsync(
        ModInfo mod, string gameDir, CancellationToken ct = default)
    {
        try
        {
            var dest = InstallDir(gameDir, mod);
            if (Directory.Exists(dest))
            {
                Directory.Delete(dest, recursive: true);
                return Task.FromResult((true, $"removed Mods\\{mod.Id}; it stops loading next time you start the game."));
            }
            return Task.FromResult((true, $"code mod '{mod.Id}' was not installed."));
        }
        catch (IOException ex)
        {
            return Task.FromResult((false, $"could not remove code mod '{mod.Id}': {ex.Message} (is the game running?)"));
        }
        catch (Exception ex)
        {
            return Task.FromResult((false, $"could not remove code mod '{mod.Id}': {ex.Message}"));
        }
    }

    /// <summary>True for the download/temp artefacts that must never be copied into the game.</summary>
    private static bool IsTransient(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
    }
}
