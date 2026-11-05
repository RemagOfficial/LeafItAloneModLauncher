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
        if (req.GameNotRunning && IsGameRunning())
            return $"\"{GameProcessName}\" is running - close the game first.";
        if (req.SafeBase && !SafeBaseInstalled())
            return "safe-base is not installed - install it before applying or reverting.";
        if (!forRevert && req.Python && string.IsNullOrEmpty(PythonPath))
            return "no Python interpreter found - set LEAF_PYTHON or add the bundled runtime.";
        return null;
    }

    /// <summary>
    /// The L4 sequence's per-mod decision, kept pure so it is unit-testable without a
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

    public Task<ProcessResult> ApplyAsync(ModInfo mod)
        => _runner.RunPowerShellScriptAsync(mod.ApplyScript, BuildApplyArgs(mod), workingDir: mod.Folder);

    public Task<ProcessResult> RevertAsync(ModInfo mod)
        => _runner.RunPowerShellScriptAsync(mod.RevertScript, BuildRevertArgs(mod), workingDir: mod.Folder);
}
