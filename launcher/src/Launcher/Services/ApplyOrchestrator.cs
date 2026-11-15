using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LeafItAlone.Launcher.Models;

namespace LeafItAlone.Launcher.Services;

/// <summary>What the Apply &amp; Launch sequence should do with a mod, given its state.</summary>
public enum ModOperation { None, Apply, Revert }

/// <summary>
/// Turns a mod's manifest into a real engine invocation and runs it through the
/// single <see cref="ProcessRunner"/> (the launcher never reimplements patching —
/// it drives the validated <c>apply.ps1</c> / <c>revert.ps1</c>).
///
/// <para>
/// The scripts are the authority on safety: <c>apply.ps1</c> re-checks the game
/// isn't running (exit 3), asserts the safe-base is active and its preconditions
/// are healthy, verifies the pinned Python exists (exit 5) and rolls both bundle
/// and catalog back on any failure after staging. We mirror the cheap checks here
/// only to give the user an immediate, friendly message instead of spawning a
/// process that is guaranteed to bail — never to replace the script's own gate.
/// </para>
///
/// <para>
/// <c>apply.ps1</c> declares <c>-Python</c>/<c>-Yes</c>; <c>revert.ps1</c> declares
/// only <c>-Yes</c>, so <see cref="BuildRevertArgs"/> must not append <c>-Python</c>
/// (an undeclared parameter would make PowerShell exit non-zero).
/// </para>
/// </summary>
public sealed class ApplyOrchestrator
{
    /// <summary>Process name (exe minus .exe) of the running game.</summary>
    public const string GameProcessName = "Leaf it Alone";

    private readonly ProcessRunner _runner;

    public ApplyOrchestrator(ProcessRunner runner) => _runner = runner;

    /// <summary>Interpreter resolved by <see cref="PythonLocator"/>; null when none found.</summary>
    public string? PythonPath { get; set; }

    /// <summary>
    /// Applies/removes CODE mods (loader-hosted folders under the game's <c>Mods\</c>). Owned
    /// here so <see cref="ApplyAsync"/> / <see cref="RevertAsync"/> can branch on mod kind.
    /// </summary>
    public CodeModService CodeMods { get; } = new();

    /// <summary>
    /// Resolves the game folder a code mod installs into, set by the VM from the safe-base
    /// manifest. Null until wired (or when no game exe is recorded), in which case a code-mod
    /// apply is refused with a readable message rather than guessing a path.
    /// </summary>
    public Func<string?>? GameDirResolver { get; set; }

    /// <summary>
    /// whether the resolved interpreter was probed and can import UnityPy. Defaults to
    /// true so a caller that never probes (or a mod that needs no Python) is unaffected; the
    /// VM sets it from <see cref="PythonHealth"/>. When false, a Python-requiring apply is
    /// blocked with a message that points at the Setup guide rather than silently failing.
    /// </summary>
    public bool PythonReady { get; set; } = true;

    /// <summary>
    /// Mod sources: hook the VM sets so a remote mod's missing files are healed
    /// before <see cref="ApplyAsync"/> shells out to <c>apply.ps1</c>. The hook receives
    /// the mod, returns a short message the caller logs, and Ok=false aborts the apply.
    /// Left null in tests and for built-in-mod-only installs.
    /// </summary>
    public Func<ModInfo, Task<(bool Ok, string Message)>>? EnsurePresentHook { get; set; }

    public static string SafeBaseRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeafItAloneSafe");

    public static bool SafeBaseInstalled() => File.Exists(Path.Combine(SafeBaseRoot, "base.psm1"));

    public static bool IsGameRunning()
    {
        try { return Process.GetProcessesByName(GameProcessName).Length > 0; }
        catch { return false; }   // never block an apply on a transient process-enumeration failure
    }

    /// <summary>
    /// A human-readable reason to hold off, or null if we can proceed. These match
    /// what the scripts enforce themselves, so a "pass" here only avoids a doomed
    /// spawn; the script still re-checks everything.
    /// </summary>
    public string? BlockingReason(ModInfo mod, bool forRevert)
    {
        var req = mod.Manifest.Requires;
        // A running game blocks a code mod too: the loader keeps the hosted DLL open, so the
        // install/remove copy would fail - force the check even if the manifest omits it.
        if ((req.GameNotRunning || mod.IsCodeMod) && IsGameRunning())
            return $"\"{GameProcessName}\" is running - close the game first.";
        if (req.SafeBase && !SafeBaseInstalled())
            return "safe-base is not installed - install it before applying or reverting.";
        // Code mods are hosted by the loader (a file copy), not patched by scripts, so the
        // Python gate never applies to them.
        if (mod.IsCodeMod)
            return null;
        if (!forRevert && req.Python && string.IsNullOrEmpty(PythonPath))
            return "no Python interpreter found - open Setup to install Python 3.12 (amd64) + UnityPy.";
        if (!forRevert && req.Python && !PythonReady)
            return "Python is installed but UnityPy is missing - open Setup to add it (python -m pip install UnityPy).";
        return null;
    }

    /// <summary>
    /// The Apply &amp; Launch sequence's per-mod decision, kept pure so it is unit-testable without a
    /// running game: an enabled mod whose state differs from what is applied must be
    /// applied; a disabled mod that is still applied must be reverted; anything already
    /// in sync needs nothing.
    /// </summary>
    public static ModOperation Plan(bool enabled, bool isApplied, bool isPending)
    {
        if (enabled && isPending) return ModOperation.Apply;
        if (!enabled && isApplied) return ModOperation.Revert;
        return ModOperation.None;
    }

    public IReadOnlyList<string> BuildApplyArgs(ModInfo mod)
    {
        var args = new List<string>(mod.Manifest.Apply.Args);
        EnsureYes(args);
        // apply.ps1 is the only one that takes -Python; add it when we resolved an
        // interpreter and the manifest didn't already hard-code the flag.
        if (mod.Manifest.Requires.Python
            && !string.IsNullOrEmpty(PythonPath)
            && !args.Contains("-Python", StringComparer.OrdinalIgnoreCase))
        {
            args.Add("-Python");
            args.Add(PythonPath!);
        }
        return args;
    }

    public IReadOnlyList<string> BuildRevertArgs(ModInfo mod)
    {
        var args = new List<string>(mod.Manifest.Revert.Args);
        EnsureYes(args);   // revert.ps1 has no -Python; adding it would error
        return args;
    }

    private static void EnsureYes(List<string> args)
    {
        if (!args.Contains("-Yes", StringComparer.OrdinalIgnoreCase)) args.Add("-Yes");
    }

    public async Task<ProcessResult> ApplyAsync(ModInfo mod)
    {
        // Mod sources: heal any missing/mismatched remote files before running anything.
        // A built-in mod has no SourceKey so the hook short-circuits to Ok; a remote
        // mod that fails verification aborts the apply with a readable Fault so the
        // user is never left running scripts we can't account for.
        if (EnsurePresentHook is not null)
        {
            var (ok, message) = await EnsurePresentHook(mod).ConfigureAwait(false);
            if (!ok) return new ProcessResult(-1, TimedOut: false, Fault: message);
        }
        // A code mod is installed by copying its folder into the game's Mods\ tree for the
        // loader to host; there is no apply.ps1 to shell out to.
        if (mod.IsCodeMod)
            return await ApplyCodeModAsync(mod).ConfigureAwait(false);
        return await _runner.RunPowerShellScriptAsync(mod.ApplyScript, BuildApplyArgs(mod), workingDir: mod.Folder).ConfigureAwait(false);
    }

    public async Task<ProcessResult> RevertAsync(ModInfo mod)
    {
        if (mod.IsCodeMod)
            return await RevertCodeModAsync(mod).ConfigureAwait(false);
        return await _runner.RunPowerShellScriptAsync(mod.RevertScript, BuildRevertArgs(mod), workingDir: mod.Folder).ConfigureAwait(false);
    }

    private async Task<ProcessResult> ApplyCodeModAsync(ModInfo mod)
    {
        var gameDir = GameDirResolver?.Invoke();
        if (string.IsNullOrWhiteSpace(gameDir))
            return new ProcessResult(-1, TimedOut: false,
                Fault: "could not find the game folder to install the code mod into - install the safe-base first (it records the game path).");
        var (ok, message) = await CodeMods.EnableAsync(mod, gameDir!).ConfigureAwait(false);
        return ok ? new ProcessResult(0, TimedOut: false) : new ProcessResult(-1, TimedOut: false, Fault: message);
    }

    private async Task<ProcessResult> RevertCodeModAsync(ModInfo mod)
    {
        var gameDir = GameDirResolver?.Invoke();
        if (string.IsNullOrWhiteSpace(gameDir))
            return new ProcessResult(-1, TimedOut: false,
                Fault: "could not find the game folder to remove the code mod from - install the safe-base first.");
        var (ok, message) = await CodeMods.DisableAsync(mod, gameDir!).ConfigureAwait(false);
        return ok ? new ProcessResult(0, TimedOut: false) : new ProcessResult(-1, TimedOut: false, Fault: message);
    }
}
