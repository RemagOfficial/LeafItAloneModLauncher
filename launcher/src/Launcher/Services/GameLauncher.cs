using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// Starts the game from the exe path <c>safe-base/install.ps1</c> detected and wrote
/// to <c>%LOCALAPPDATA%\LeafItAloneSafe\manifest.json</c> (<c>gameExe</c>) — the same
/// value <c>apply.ps1</c> uses to locate the bundle. The launcher never hard-codes a
/// drive (the game lives on <c>B:</c> here), so this is the single source of truth.
///
/// <para>
/// We start the exe directly (as a double-click would). safe-base keeps the game
/// offline via the outbound firewall rule and the stub <c>steam_api64.dll</c>, so the
/// title runs without reaching the leaderboards; Steam itself is a separate process,
/// unaffected by launching the exe. Fire-and-forget: we do not redirect or wait on the
/// game's stdout, and <see cref="ProcessStartInfo.UseShellExecute"/> lets the shell
/// apply any launch conventions.
/// </para>
/// </summary>
public static class GameLauncher
{
    public static string ManifestPath => Path.Combine(ApplyOrchestrator.SafeBaseRoot, "manifest.json");

    /// <summary>
    /// The recorded game exe, or null if the manifest is missing/unparseable, has no
    /// <c>gameExe</c>, or the file no longer exists (game moved/uninstalled).
    /// </summary>
    public static string? ResolveGameExe() => ResolveGameExeFrom(ManifestPath);

    /// <summary>
    /// L9: a user override wins when it names a file that exists; otherwise fall back to
    /// the safe-base manifest. So a stale/deleted override never blocks launching - it just
    /// reverts to the normal source of truth.
    /// </summary>
    public static string? ResolveGameExe(string? overrideExe) =>
        !string.IsNullOrWhiteSpace(overrideExe) && File.Exists(overrideExe)
            ? overrideExe
            : ResolveGameExe();

    /// <summary>
    /// Read + validate a <c>manifest.json</c> at an explicit path (the real method just
    /// passes the safe-base location). Separated so the parsing is unit-testable without
    /// touching the user's live manifest.
    /// </summary>
    public static string? ResolveGameExeFrom(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return null;
            var root = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
            var exe = root?["gameExe"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(exe)) return null;
            return File.Exists(exe) ? exe : null;
        }
        catch
        {
            return null;   // a malformed manifest must not throw across the UI
        }
    }

    /// <summary>
    /// Launch the game. An optional user <paramref name="exeOverride"/> (L9 Settings) wins
    /// when it exists; otherwise the safe-base manifest's <c>gameExe</c> is used. Returns a
    /// (success, human message) pair for the status bar and log. Never throws.
    /// </summary>
    public static (bool Ok, string Message) Launch(string? exeOverride = null)
    {
        var exe = ResolveGameExe(exeOverride);
        if (exe is null)
            return (false, "could not find the game - safe-base manifest.json has no live gameExe (install safe-base, or the game may have moved).");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe),
                UseShellExecute = true,
                CreateNoWindow = false,
            };
            Process.Start(psi);
            return (true, $"launched {exe}");
        }
        catch (Exception ex)
        {
            return (false, "failed to start the game: " + ex.Message);
        }
    }
}
