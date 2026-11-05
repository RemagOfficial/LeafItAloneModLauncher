using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// A snapshot of the safe-base install, read straight from the manifest the
/// framework writes at <c>%LOCALAPPDATA%\LeafItAloneSafe\manifest.json</c>. This
/// is the cheap, structured signal the Dashboard uses (no subprocess); the
/// authoritative health check is <c>status.ps1</c>, which <see cref="SafeBaseService"/>
/// can also run and summarise.
/// </summary>
public sealed record SafeBaseInfo(bool Installed, string InstalledAt, string Milestone, string? GameExe)
{
    public static SafeBaseInfo NotInstalled { get; } = new(false, "", "", null);
}

/// <summary>
/// Drives the safe-base lifecycle by shelling out to the already-tested scripts in
/// the engine folder (dev <c>safe-base/</c> or published <c>tools/</c>) — the launcher
/// never reimplements any of it (plan: "absorb the UX, reuse the engines").
///
/// <para>
/// <c>install.ps1</c> and <c>uninstall-all.ps1</c> both self-elevate through UAC and, in
/// the non-elevated parent we spawn, do <c>Start-Process -Verb RunAs -PassThru</c> then
/// <c>WaitForExit()</c> and re-<c>exit</c> the elevated child's code. So the parent's exit
/// code we read here is the real outcome, even though the child prints into its own
/// console window we cannot stream. We pass <c>-Yes</c> so neither script blocks on an
/// interactive <c>Read-Host</c> (we have no console stdin).
/// </para>
/// </summary>
public sealed class SafeBaseService
{
    private readonly ProcessRunner _runner;
    private readonly string _engineDir;

    public SafeBaseService(ProcessRunner runner, string engineDir)
    {
        _runner = runner;
        _engineDir = engineDir;
    }

    /// <summary>Wall-clock budget for an install / uninstall: includes the human UAC pause.</summary>
    public static readonly TimeSpan LifecycleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>The manifest the framework writes on install (and removes on uninstall).</summary>
    public static string ManifestPath => Path.Combine(ApplyOrchestrator.SafeBaseRoot, "manifest.json");

    private string Script(string name) => Path.Combine(_engineDir, name);

    /// <summary>Read the live install state from the manifest without running anything.</summary>
    public SafeBaseInfo Read() => ReadFrom(ManifestPath);

    /// <summary>
    /// Parse a safe-base manifest into a <see cref="SafeBaseInfo"/>. Pure and path-driven so
    /// it is testable against a temp file. A missing / unreadable / malformed manifest (or one
    /// without a live <c>gameExe</c>) reports <see cref="SafeBaseInfo.NotInstalled"/>.
    /// </summary>
    public static SafeBaseInfo ReadFrom(string manifestPath)
    {
        try
        {
            if (!File.Exists(manifestPath)) return SafeBaseInfo.NotInstalled;
            var root = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
            if (root is null) return SafeBaseInfo.NotInstalled;

            string Text(string key) => root[key]?.GetValue<string>() ?? "";
            var gameExe = Text("gameExe");
            // A manifest without a recorded, still-present game exe is not a usable install.
            string? exe = string.IsNullOrWhiteSpace(gameExe) ? null : gameExe;
            return new SafeBaseInfo(true, Text("installedAt"), Text("milestone"), exe);
        }
        catch
        {
            return SafeBaseInfo.NotInstalled;
        }
    }

    /// <summary>
    /// Pull the final <c>VERDICT:</c> line out of <c>status.ps1</c>'s output for a
    /// one-line health summary. Pure so it can be unit-tested from captured lines.
    /// Returns the verdict text (without the leading label) or null when absent.
    /// </summary>
    public static string? ExtractVerdict(IEnumerable<string> lines) =>
        lines.FirstOrDefault(l => l.TrimStart().StartsWith("VERDICT:", StringComparison.OrdinalIgnoreCase))?
             .Split(':', 2).ElementAtOrDefault(1)?.Trim();

    /// <summary>Run <c>status.ps1</c> (read-only, no admin): streams to the log and returns the verdict line.</summary>
    public async Task<(ProcessResult Result, string? Verdict)> RunStatusAsync()
    {
        var captured = await RunCapturingAsync(Script("status.ps1"), null, ProcessRunner.DefaultTimeout);
        return (captured.Result, ExtractVerdict(captured.Lines));
    }

    /// <summary>Install the safe-base (self-elevates; waits on the elevated child's exit code).</summary>
    public Task<ProcessResult> InstallAsync() =>
        _runner.RunPowerShellScriptAsync(Script("install.ps1"), new[] { "-Yes" },
            workingDir: _engineDir, timeout: LifecycleTimeout);

    /// <summary>Revert every registered mod, then uninstall the safe-base (self-elevates).</summary>
    public Task<ProcessResult> UninstallAllAsync() =>
        _runner.RunPowerShellScriptAsync(Script("uninstall-all.ps1"), new[] { "-Yes" },
            workingDir: _engineDir, timeout: LifecycleTimeout);

    /// <summary>Run a script and collect every streamed line, so a caller can parse a signal from it.</summary>
    private async Task<(ProcessResult Result, List<string> Lines)> RunCapturingAsync(
        string script, IReadOnlyList<string>? args, TimeSpan timeout)
    {
        var lines = new List<string>();
        var gate = new object();
        void Collect(string l) { lock (gate) lines.Add(l); }

        _runner.LineReceived += Collect;
        try
        {
            var res = await _runner.RunPowerShellScriptAsync(script, args, workingDir: _engineDir, timeout: timeout);
            lock (gate) return (res, new List<string>(lines));
        }
        finally
        {
            _runner.LineReceived -= Collect;
        }
    }
}
