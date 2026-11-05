using System;
using System.IO;
using System.Linq;
using LeafItAlone.Launcher.Models;
using LeafItAlone.Launcher.Services;

namespace LeafItAlone.Launcher.Tests;

/// <summary>
/// L1 headless checks: discovery, manifest round-trip, env-path expansion and the
/// subprocess runner — all without launching WPF or touching the game. Exits 0 on
/// success, 1 on any failed assertion, so it doubles as a build gate.
/// </summary>
internal static class Program
{
    private static int _failures;

    private static int Main()
    {
        Console.WriteLine("Launcher L1 headless checks");
        Console.WriteLine("===========================");

        CheckRepoAndDiscovery();
        CheckEnvPathExpansion();
        ProcessRunnerCheck().GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL CHECKS PASSED" : $"{_failures} CHECK(S) FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void CheckRepoAndDiscovery()
    {
        var locator = new RepoLocator();
        // Start at the test binary dir; it walks up to the repo root (content-mods + safe-base).
        bool found = locator.TryLocate(AppContext.BaseDirectory);
        Assert(found, "RepoLocator found the content root", locator.LastError ?? "");
        Assert(locator.ModsRoot is not null && Directory.Exists(locator.ModsRoot),
            "ModsRoot exists", locator.ModsRoot ?? "null");

        var result = new ModDiscovery().Discover(locator.ModsRoot!);
        foreach (var err in result.Errors)
            Console.WriteLine("  discovery error: " + err);
        Assert(result.Errors.Count == 0, "no manifest parse errors", string.Join("; ", result.Errors));

        var bag = result.Mods.FirstOrDefault(m => m.Id == "bag-capacity");
        Assert(bag is not null, "bag-capacity discovered",
            "ids: " + string.Join(", ", result.Mods.Select(m => m.Id)));
        if (bag is null) return;

        Assert(bag.Version == "1.2.0", "version is 1.2.0", bag.Version);
        Assert(bag.Order == 10, "order is 10", bag.Order.ToString());
        Assert(bag.Manifest.Config.Fields.Count == 6, "6 config fields", bag.Manifest.Config.Fields.Count.ToString());

        var mode = bag.Manifest.Config.Fields.FirstOrDefault(f => f.Key == "mode");
        Assert(mode is not null && mode.Type == FieldTypes.Enum
               && mode.Options.SequenceEqual(new[] { "multiplier", "sizes" }),
            "mode is an enum [multiplier,sizes]", mode?.Type ?? "missing");

        var mult = bag.Manifest.Config.Fields.FirstOrDefault(f => f.Key == "multiplier");
        Assert(mult?.When is not null && mult.When.Key == "mode"
               && Equals(mult.When.EqualsValue?.GetValue<string>(), "multiplier"),
            "multiplier gated on mode==multiplier", mult?.When?.EqualsValue?.ToString() ?? "none");

        var giant = bag.Manifest.Config.Fields.FirstOrDefault(f => f.Key == "sizes.giant");
        Assert(giant?.Default?.GetValue<int>() == 1600, "sizes.giant default 1600",
            giant?.Default?.ToString() ?? "none");

        Assert(bag.Manifest.Requires.SafeBase && bag.Manifest.Requires.GameNotRunning && bag.Manifest.Requires.Python,
            "requires all true", bag.RequiresSummary);
        Assert(bag.ApplyScript.EndsWith("apply.ps1"), "apply script resolves", bag.ApplyScript);
    }

    private static void CheckEnvPathExpansion()
    {
        const string raw = @"%LOCALAPPDATA%\LeafItAloneSafe\bag-capacity.json";
        var expanded = EnvPaths.Expand(raw);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        // The token must resolve to the real LOCALAPPDATA root (dir contents may
        // not exist yet on a clean machine, but the base always does).
        bool ok = !expanded.Contains("%")
                  && expanded.StartsWith(local, StringComparison.OrdinalIgnoreCase)
                  && Directory.Exists(local);
        Assert(ok, "configPath expands %LOCALAPPDATA% to the real root", expanded);
    }

    private static async System.Threading.Tasks.Task ProcessRunnerCheck()
    {
        var runner = new ProcessRunner();
        var lines = new System.Collections.Generic.List<string>();
        runner.LineReceived += l => { lock (lines) lines.Add(l); };

        string ps = Environment.GetFolderPath(Environment.SpecialFolder.System)
                    + "\\WindowsPowerShell\\v1.0\\powershell.exe";
        var res = await runner.RunAsync(ps,
            new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "Write-Output 'ping'; exit 0" },
            timeout: TimeSpan.FromSeconds(20));

        Assert(res.Success, "runner: exit code 0", $"exit {res.ExitCode} timeout={res.TimedOut} fault={res.Fault}");
        lock (lines)
            Assert(lines.Any(l => l.Contains("ping")), "runner: streamed stdout line", string.Join("|", lines));
    }

    private static void Assert(bool ok, string what, string detail = "")
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(ok ? "" : "  -> " + detail)}");
        if (!ok) _failures++;
    }
}
