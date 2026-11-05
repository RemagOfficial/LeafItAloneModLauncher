using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using LeafItAlone.Launcher.Models;
using LeafItAlone.Launcher.Services;
using LeafItAlone.Launcher.ViewModels;

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
        Console.WriteLine("Launcher L1/L2 headless checks");
        Console.WriteLine("===========================");

        CheckRepoAndDiscovery();
        CheckEnvPathExpansion();
        CheckConfigStoreRoundTrip();
        CheckConfigEditorRoundTrip();
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

    private static void CheckConfigStoreRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leaf-l2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "cfg.json");
        try
        {
            // A config with keys the launcher schema does NOT describe (must survive)
            // plus a nested note inside an object we do edit.
            File.WriteAllText(path,
                "{\"_comment\":\"keep me\",\"_applyHelper\":\"C:\\\\x.ps1\",\"mode\":\"multiplier\"," +
                "\"multiplier\":2.0,\"sizes\":{\"small\":100,\"_note\":\"nested keep\",\"medium\":400,\"big\":1000,\"giant\":1600}}");

            var root = ConfigStore.Load(path);
            Assert(Equals(ConfigStore.GetByPath(root, "sizes.small")?.GetValue<int>(), 100),
                "dotted get sizes.small", "read failed");

            string shaBefore = ConfigStore.ComputeSha(path);
            ConfigStore.SetByPath(root, "multiplier", JsonValue.Create(3.5));
            ConfigStore.SetByPath(root, "sizes.small", JsonValue.Create(250));
            ConfigStore.Save(path, root);

            var reloaded = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert(Equals(reloaded["_comment"]?.GetValue<string>(), "keep me"), "_comment preserved", reloaded["_comment"]?.ToString() ?? "gone");
            Assert(reloaded["_applyHelper"] is not null, "_applyHelper preserved");
            Assert(Equals(reloaded["sizes"]?["_note"]?.GetValue<string>(), "nested keep"), "nested sibling preserved");
            Assert(Equals(reloaded["multiplier"]?.GetValue<double>(), 3.5), "multiplier written", reloaded["multiplier"]?.ToString() ?? "?");
            Assert(Equals(reloaded["sizes"]?["small"]?.GetValue<int>(), 250), "sizes.small written");
            Assert(Equals(reloaded["sizes"]?["medium"]?.GetValue<int>(), 400), "sizes.medium untouched");
            Assert(ConfigStore.ComputeSha(path) != shaBefore, "sha changes after edit");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static void CheckConfigEditorRoundTrip()
    {
        var loc = new RepoLocator();
        if (!loc.TryLocate(AppContext.BaseDirectory)) { Assert(false, "editor test located content root", loc.LastError ?? ""); return; }
        var result = new ModDiscovery().Discover(loc.ModsRoot!);
        var bag = result.Mods.FirstOrDefault(m => m.Id == "bag-capacity");
        if (bag is null) { Assert(false, "editor test found bag-capacity"); return; }

        var dir = Path.Combine(Path.GetTempPath(), "leaf-l2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "bag-capacity.json");
        try
        {
            File.WriteAllText(path,
                "{\"_comment\":\"keep\",\"_applyHelper\":\"C:\\\\h.ps1\",\"mode\":\"multiplier\",\"multiplier\":2.0," +
                "\"sizes\":{\"small\":100,\"medium\":400,\"big\":1000,\"giant\":1600}}");

            bag.Manifest.ConfigPath = path;   // point the editor at a temp store, never the user's live file
            var editor = new ConfigEditorViewModel(bag);
            Assert(editor.Fields.Count == 6, "editor built 6 fields", editor.Fields.Count.ToString());

            // `when` gating: sizes.* hidden while mode==multiplier, multiplier visible.
            var multiplier = editor.Fields.First(f => f.Key == "multiplier");
            var small = editor.Fields.First(f => f.Key == "sizes.small");
            Assert(multiplier.IsVisible, "multiplier visible in multiplier mode");
            Assert(!small.IsVisible, "sizes.small hidden in multiplier mode");

            // Switching mode re-gates visibility.
            editor.Fields.First(f => f.Key == "mode").Str = "sizes";
            Assert(small.IsVisible && !multiplier.IsVisible, "visibility re-gates on mode change");

            // Edit a value and save; confirm the live file + extras + pending.
            small.Num = 333;
            bool pendingBefore = editor.IsPending;
            editor.Save();
            var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert(Equals(saved["sizes"]?["small"]?.GetValue<int>(), 333), "editor wrote sizes.small");
            Assert(Equals(saved["_comment"]?.GetValue<string>(), "keep"), "editor preserved _comment");
            Assert(Equals(saved["_applyHelper"]?.GetValue<string>(), @"C:\h.ps1"), "editor preserved _applyHelper");
            Assert(pendingBefore && editor.IsPending, "editor reports pending until applied (L3)");

            // PowerShell ConvertFrom-Json must accept the file the launcher wrote (plan verify).
            var runner = new ProcessRunner();
            string ps = Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\WindowsPowerShell\\v1.0\\powershell.exe";
            int exit = runner.RunAsync(ps, new[]
                {
                    "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
                    "$null = Get-Content -LiteralPath '" + path + "' -Raw | ConvertFrom-Json; exit 0"
                }, timeout: TimeSpan.FromSeconds(20)).GetAwaiter().GetResult().ExitCode;
            Assert(exit == 0, "ConvertFrom-Json parses the saved config", $"ps exit {exit}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
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
