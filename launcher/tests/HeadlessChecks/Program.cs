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
        Console.WriteLine("Launcher L1/L2/L3/L4 headless checks");
        Console.WriteLine("================================");

        CheckRepoAndDiscovery();
        CheckEnvPathExpansion();
        CheckConfigStoreRoundTrip();
        CheckConfigEditorRoundTrip();
        CheckPendingLifecycle();
        CheckOrchestratorArgs();
        CheckSequencePlan();
        CheckGameExeResolution();
        ProcessRunnerCheck().GetAwaiter().GetResult();
        OrchestratorRunCheck().GetAwaiter().GetResult();

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
            // Pending is enabled-aware (L3): a disabled mod is never pending, so opt in first.
            editor.Enabled = true;
            small.Num = 333;
            bool pendingBefore = editor.IsPending;   // enabled + nothing applied yet
            editor.Save();
            var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert(Equals(saved["sizes"]?["small"]?.GetValue<int>(), 333), "editor wrote sizes.small");
            Assert(Equals(saved["_comment"]?.GetValue<string>(), "keep"), "editor preserved _comment");
            Assert(Equals(saved["_applyHelper"]?.GetValue<string>(), @"C:\h.ps1"), "editor preserved _applyHelper");
            Assert(pendingBefore && editor.IsPending, "enabled & unapplied config stays pending");

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

    /// <summary>
    /// L3 pending model (enabled-aware), exercised without WPF: the badge must track
    /// "would an Apply/Revert change the game?" - not just "does the file differ from
    /// what was applied". Mirrors exactly what the VM does (Apply saves, then records
    /// the applied sha; Revert clears it).
    /// </summary>
    private static void CheckPendingLifecycle()
    {
        var loc = new RepoLocator();
        if (!loc.TryLocate(AppContext.BaseDirectory)) { Assert(false, "pending test located content root", loc.LastError ?? ""); return; }
        var bag = new ModDiscovery().Discover(loc.ModsRoot!).Mods.FirstOrDefault(m => m.Id == "bag-capacity");
        if (bag is null) { Assert(false, "pending test found bag-capacity"); return; }

        var dir = Path.Combine(Path.GetTempPath(), "leaf-l3p-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "bag-capacity.json");
        try
        {
            File.WriteAllText(path,
                "{\"mode\":\"multiplier\",\"multiplier\":2.0," +
                "\"sizes\":{\"small\":100,\"medium\":400,\"big\":1000,\"giant\":1600}}");
            bag.Manifest.ConfigPath = path;

            var editor = new ConfigEditorViewModel(bag);
            editor.ApplyState(new ModState { Enabled = false, LastAppliedConfigSha = "" });
            Assert(!editor.IsPending && !editor.IsApplied, "disabled & never applied -> idle (no badge)");

            editor.Enabled = true;
            Assert(editor.IsPending && !editor.IsApplied, "enabled & not yet applied -> pending");

            // The VM's apply sequence: save, then record the applied sha.
            editor.Save();
            editor.MarkApplied(editor.CurrentConfigSha);
            Assert(!editor.IsPending && editor.IsApplied, "applied & unchanged -> clean (applied)");

            // Editing then saving re-dirties it (this is what Apply's Save would push).
            editor.Fields.First(f => f.Key == "multiplier").Num = 4.0;
            editor.Save();
            Assert(editor.IsPending && editor.IsApplied, "config changed after apply -> pending");

            // Disabling an applied mod owes a revert, so it is pending again.
            editor.Enabled = false;
            Assert(editor.IsPending && editor.IsApplied, "disabled while applied -> pending (revert owed)");

            // The VM's revert sequence: clear the applied sha.
            editor.MarkReverted();
            editor.Enabled = false;
            Assert(!editor.IsPending && !editor.IsApplied, "after revert -> idle (nothing applied)");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Command-line construction must match what each script declares: apply gets
    /// <c>-Yes -Python &lt;resolved&gt;</c>; revert gets <c>-Yes</c> only (it has no
    /// <c>-Python</c> parameter, so passing one would make PowerShell exit non-zero).
    /// Also that a mod with no requirements is never blocked.
    /// </summary>
    private static void CheckOrchestratorArgs()
    {
        var mod = FakeMod(Path.GetTempPath(), "apply.ps1", "revert.ps1", python: true);
        var ops = new ApplyOrchestrator(new ProcessRunner()) { PythonPath = @"C:\py\python.exe" };

        var apply = ops.BuildApplyArgs(mod);
        Assert(apply.Contains("-Yes"), "apply args include -Yes", string.Join(" ", apply));
        Assert(apply.Contains("-Python") && apply.Contains(@"C:\py\python.exe"),
            "apply args include -Python <path>", string.Join(" ", apply));

        var revert = ops.BuildRevertArgs(mod);
        Assert(revert.Contains("-Yes"), "revert args include -Yes", string.Join(" ", revert));
        Assert(!revert.Contains("-Python"), "revert args do NOT include -Python", string.Join(" ", revert));

        // No requirement was flagged off, so nothing should block it here.
        var bare = FakeMod(Path.GetTempPath(), "apply.ps1", "revert.ps1", python: false);
        Assert(ops.BlockingReason(bare, forRevert: false) is null, "mod with no requirements is not blocked");
    }

    /// <summary>
    /// End-to-end through the real subprocess runner, but against FAKE engine scripts
    /// in a temp folder - never the game. Proves the orchestrator launches PowerShell,
    /// passes the args, streams output, and reports the exit code faithfully (0 = ok,
    /// 7 = a scripted failure the caller must treat as "game untouched").
    /// </summary>
    private static async System.Threading.Tasks.Task OrchestratorRunCheck()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leaf-l3r-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "apply_ok.ps1"),
                "param([switch]$Yes,[string]$Python)\n" +
                "\"Yes=$Yes Python=$Python\" | Out-File -FilePath 'apply_args.txt' -Encoding ascii\n" +
                "Write-Output 'fake-apply-ran'\nexit 0\n");
            File.WriteAllText(Path.Combine(dir, "apply_fail.ps1"),
                "param([switch]$Yes,[string]$Python)\nexit 7\n");
            File.WriteAllText(Path.Combine(dir, "revert_ok.ps1"),
                "param([switch]$Yes)\n" +
                "\"Yes=$Yes\" | Out-File -FilePath 'revert_args.txt' -Encoding ascii\n" +
                "Write-Output 'fake-revert-ran'\nexit 0\n");

            var lines = new System.Collections.Generic.List<string>();
            var runner = new ProcessRunner();
            runner.LineReceived += l => { lock (lines) lines.Add(l); };
            var ops = new ApplyOrchestrator(runner) { PythonPath = "PYTEST" };

            var okMod = FakeMod(dir, "apply_ok.ps1", "revert_ok.ps1", python: true);

            var applied = await ops.ApplyAsync(okMod);
            Assert(applied.Success, "fake apply exited 0", $"exit {applied.ExitCode} timeout={applied.TimedOut} fault={applied.Fault}");
            Assert(File.Exists(Path.Combine(dir, "apply_args.txt")), "fake apply actually ran");
            string applyArgs = File.Exists(Path.Combine(dir, "apply_args.txt"))
                ? File.ReadAllText(Path.Combine(dir, "apply_args.txt")) : "";
            Assert(applyArgs.Contains("Yes=True") && applyArgs.Contains("PYTEST"),
                "fake apply received -Yes and -Python", applyArgs.Trim());
            lock (lines) Assert(lines.Any(l => l.Contains("fake-apply-ran")), "apply output streamed to log", string.Join("|", lines));

            var reverted = await ops.RevertAsync(okMod);
            Assert(reverted.Success, "fake revert exited 0 (no -Python error)", $"exit {reverted.ExitCode}");
            Assert(File.Exists(Path.Combine(dir, "revert_args.txt")), "fake revert actually ran");

            var failMod = FakeMod(dir, "apply_fail.ps1", "revert_ok.ps1", python: true);
            var failed = await ops.ApplyAsync(failMod);
            Assert(!failed.Success && failed.ExitCode == 7, "failing apply reports exit 7 as failure", $"exit {failed.ExitCode} success={failed.Success}");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// The L4 "Apply &amp; Launch" per-mod decision (pure), across the state matrix the
    /// pending model can produce. Proves the sequence applies what should be live,
    /// reverts what was switched off, and leaves in-sync mods alone (so it never churns
    /// the game for no reason).
    /// </summary>
    private static void CheckSequencePlan()
    {
        Assert(ApplyOrchestrator.Plan(enabled: true, isApplied: false, isPending: true) == ModOperation.Apply,
            "enabled + not yet applied -> Apply");
        Assert(ApplyOrchestrator.Plan(enabled: true, isApplied: true, isPending: true) == ModOperation.Apply,
            "enabled + edited after apply -> Apply");
        Assert(ApplyOrchestrator.Plan(enabled: true, isApplied: true, isPending: false) == ModOperation.None,
            "enabled + applied + unchanged -> None");
        Assert(ApplyOrchestrator.Plan(enabled: false, isApplied: true, isPending: true) == ModOperation.Revert,
            "disabled + still applied -> Revert");
        Assert(ApplyOrchestrator.Plan(enabled: false, isApplied: false, isPending: false) == ModOperation.None,
            "disabled + never applied -> None");
    }

    /// <summary>
    /// The launcher starts the game from safe-base's manifest <c>gameExe</c>, but only
    /// when that path actually exists. A missing/blank/malformed manifest resolves to
    /// null (surfaced as a friendly message) instead of throwing or launching a stale path.
    /// </summary>
    private static void CheckGameExeResolution()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leaf-l4-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var realExe = Path.Combine(dir, "Leaf it Alone.exe");
            File.WriteAllText(realExe, "stub");

            string WriteManifest(string name, JsonObject obj)
            {
                var p = Path.Combine(dir, name);
                File.WriteAllText(p, obj.ToJsonString());
                return p;
            }

            var ok = WriteManifest("ok.json", new JsonObject { ["gameExe"] = realExe });
            Assert(Equals(GameLauncher.ResolveGameExeFrom(ok), realExe),
                "resolves a live gameExe", GameLauncher.ResolveGameExeFrom(ok) ?? "null");

            var moved = WriteManifest("moved.json", new JsonObject { ["gameExe"] = Path.Combine(dir, "gone.exe") });
            Assert(GameLauncher.ResolveGameExeFrom(moved) is null, "gameExe that no longer exists -> null");

            var blank = WriteManifest("blank.json", new JsonObject { ["gameExe"] = "" });
            Assert(GameLauncher.ResolveGameExeFrom(blank) is null, "blank gameExe -> null");

            var noKey = WriteManifest("nokey.json", new JsonObject { ["something"] = "else" });
            Assert(GameLauncher.ResolveGameExeFrom(noKey) is null, "manifest without gameExe -> null");

            var bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(bad, "{ this is not json ");
            Assert(GameLauncher.ResolveGameExeFrom(bad) is null, "malformed manifest -> null (no throw)");

            Assert(GameLauncher.ResolveGameExeFrom(Path.Combine(dir, "absent.json")) is null,
                "missing manifest file -> null");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static ModInfo FakeMod(string folder, string apply, string revert, bool python)
    {
        var manifest = new ModManifest
        {
            Id = "fake-mod",
            Name = "Fake Mod",
            Version = "0.0.1",
            ConfigPath = "",
            Apply = new CommandSpec { Script = apply, Args = new System.Collections.Generic.List<string> { "-Yes" } },
            Revert = new CommandSpec { Script = revert, Args = new System.Collections.Generic.List<string> { "-Yes" } },
            Requires = new Requires { SafeBase = false, GameNotRunning = false, Python = python },
        };
        return new ModInfo(manifest, folder, Path.Combine(folder, "mod.json"));
    }

    private static void Assert(bool ok, string what, string detail = "")

    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}{(ok ? "" : "  -> " + detail)}");
        if (!ok) _failures++;
    }
}
