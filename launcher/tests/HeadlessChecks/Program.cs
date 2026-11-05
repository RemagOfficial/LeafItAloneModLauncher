using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Media;
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
        Console.WriteLine("Launcher L1/L2/L3/L4/L5/L6/L7/L9 headless checks");
        Console.WriteLine("================================");

        CheckRepoAndDiscovery();
        CheckSecondModManifestDriven();
        CheckEnvPathExpansion();
        CheckConfigStoreRoundTrip();
        CheckConfigEditorRoundTrip();
        CheckConfigFieldDecouple();
        CheckPendingLifecycle();
        CheckOrchestratorArgs();
        CheckSequencePlan();
        CheckGameExeResolution();
        CheckSafeBaseLifecycle();
        CheckLogColorization();
        CheckPythonHealth();
        CheckAppConfigRoundTrip();
        CheckContentRootOverride();
        CheckGameExeOverride();
        CheckThemeMath();
        CheckThemeTokenParity();
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

    /// <summary>
    /// L7's core claim: a second UnityPy mod is added purely by dropping a folder with a
    /// mod.json next to bag-capacity - no launcher code changed. Discovers the REAL
    /// content-mods tree and asserts more-leaves shows up, ordered after bag-capacity, with
    /// its manifest fully parsed. Safe: reads manifests only, never touches the game.
    /// </summary>
    private static void CheckSecondModManifestDriven()
    {
        var locator = new RepoLocator();
        if (!locator.TryLocate(AppContext.BaseDirectory) || locator.ModsRoot is null)
        {
            Assert(false, "RepoLocator found mods root for L7", locator.LastError ?? "null");
            return;
        }

        var result = new ModDiscovery().Discover(locator.ModsRoot!);
        Assert(result.Errors.Count == 0, "L7: no manifest parse errors", string.Join("; ", result.Errors));

        var leaves = result.Mods.FirstOrDefault(m => m.Id == "more-leaves");
        Assert(leaves is not null, "more-leaves discovered from its manifest alone",
            "ids: " + string.Join(", ", result.Mods.Select(m => m.Id)));
        if (leaves is null) return;

        Assert(leaves.Version == "1.3.1", "more-leaves version 1.3.1", leaves.Version);
        Assert(leaves.Order == 20, "more-leaves order 20", leaves.Order.ToString());

        // Discovery sorts by order then name: bag-capacity (10) must come before more-leaves (20).
        var ids = result.Mods.Select(m => m.Id).ToList();
        int bagIdx = ids.IndexOf("bag-capacity");
        int leavesIdx = ids.IndexOf("more-leaves");
        Assert(bagIdx >= 0 && leavesIdx > bagIdx, "applied in manifest order (bag before leaves)",
            $"bag@{bagIdx} leaves@{leavesIdx} of [{string.Join(",", ids)}]");

        Assert(leaves.Manifest.Requires.SafeBase && leaves.Manifest.Requires.GameNotRunning
               && leaves.Manifest.Requires.Python, "more-leaves requires all true", leaves.RequiresSummary);
        Assert(leaves.ApplyScript.EndsWith("apply.ps1"), "more-leaves apply script resolves", leaves.ApplyScript);

        // v1.2 is per-world: an integer density scale for each of the three worlds
        // (nested keys scales.<world>) plus a float scatter radius. Keys use dotted
        // paths so ConfigStore reads/writes them inside the live config's "scales" object.
        Assert(leaves.Manifest.Config.Fields.Count == 4, "more-leaves has 4 config fields",
            leaves.Manifest.Config.Fields.Count.ToString());
        var ht = leaves.Manifest.Config.Fields.FirstOrDefault(f => f.Key == "scales.hometown");
        Assert(ht is not null && ht.Type == FieldTypes.Int && ht.When is null
               && Equals(ht.Default?.GetValue<int>(), 2),
            "more-leaves scales.hometown is a plain int default 2",
            $"{ht?.Type ?? "missing"} def={ht?.Default?.ToString() ?? "none"} when={(ht?.When is null ? "none" : "gated")}");
        var radius = leaves.Manifest.Config.Fields.FirstOrDefault(f => f.Key == "scatterRadius");
        Assert(radius is not null && radius.Type == FieldTypes.Float
               && Equals(radius.Default?.GetValue<double>(), 0.6),
            "more-leaves scatterRadius is a float default 0.6",
            $"{radius?.Type ?? "missing"} def={radius?.Default?.ToString() ?? "none"}");
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

    /// <summary>
    /// The slider and the text box are DECOUPLED: the slider stays pinned to the manifest's
    /// safe [Min,Max] band, while the box accepts any value except 0 (and rejects non-numeric
    /// / non-finite / values that round to zero). The authoritative value can sit out of range;
    /// the slider just shows it clamped, and the saved JSON keeps the real number.
    /// Uses bag-capacity sizes.small (int, min 0, max 9999).
    /// </summary>
    private static void CheckConfigFieldDecouple()
    {
        var loc = new RepoLocator();
        if (!loc.TryLocate(AppContext.BaseDirectory)) { Assert(false, "decouple test located content root", loc.LastError ?? ""); return; }
        var bag = new ModDiscovery().Discover(loc.ModsRoot!).Mods.FirstOrDefault(m => m.Id == "bag-capacity");
        if (bag is null) { Assert(false, "decouple test found bag-capacity"); return; }

        var dir = Path.Combine(Path.GetTempPath(), "leaf-dec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "bag-capacity.json");
        try
        {
            File.WriteAllText(path, "{\"mode\":\"sizes\",\"multiplier\":2.0,\"sizes\":{\"small\":100}}");
            bag.Manifest.ConfigPath = path;
            var editor = new ConfigEditorViewModel(bag);
            var small = editor.Fields.First(f => f.Key == "sizes.small");
            Assert(small.Min == 0 && small.Max == 9999, "sizes.small safe band is 0..9999", $"{small.Min}..{small.Max}");

            // Box accepts an out-of-range value; slider view clamps, the raw value is kept.
            small.ValueText = "50000";
            Assert(small.ValueText == "50000", "box keeps out-of-range 50000", small.ValueText);
            Assert(small.Num == 9999, "slider clamps the view to max 9999", small.Num.ToString());
            Assert(Equals(small.ToNode().GetValue<int>(), 50000), "saved node keeps the real 50000", small.ToNode().ToString());

            // Box rejects 0 (reverts to the last good value), and non-numeric too.
            small.ValueText = "0";
            Assert(small.ValueText == "50000", "box rejects 0, keeps last good", small.ValueText);
            small.ValueText = "abc";
            Assert(small.ValueText == "50000", "box rejects non-numeric, keeps last good", small.ValueText);

            // Int rounding inside the band.
            small.ValueText = "200.6";
            Assert(small.ValueText == "201" && small.Num == 201, "box rounds 200.6 -> 201 int", small.ValueText + "/" + small.Num);

            // The slider path always clamps into the safe band and rewrites the box.
            small.Num = 40000;
            Assert(small.Num == 9999 && small.ValueText == "9999", "slider clamps 40000 -> 9999", small.ValueText);
            small.Num = 0;   // 0 IS reachable via the slider (a valid min); only the box forbids it
            Assert(small.ValueText == "0", "slider can still set the safe min 0", small.ValueText);

            // Saving writes the real (out-of-range) number to disk.
            small.ValueText = "12345";
            editor.Enabled = true;
            editor.Save();
            var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert(Equals(saved["sizes"]?["small"]?.GetValue<int>(), 12345), "save persists out-of-range 12345",
                   saved["sizes"]?["small"]?.ToString() ?? "<null>");
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

    /// <summary>
    /// L5 safe-base lifecycle: the manifest parse that drives the Dashboard (no subprocess)
    /// plus a real <c>status.ps1</c> run through the runner. The status run also proves that
    /// <c>Write-Host</c> output reaches our redirected stdout (status.ps1 prints its VERDICT
    /// with Write-Host, not Write-Output) and that the VERDICT line is extracted for the panel.
    /// </summary>
    private static void CheckSafeBaseLifecycle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leaf-l5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // -- manifest parse (pure) --
            var manifest = Path.Combine(dir, "manifest.json");
            File.WriteAllText(manifest, new JsonObject
            {
                ["installedAt"] = "2026-11-05T10:00:00",
                ["milestone"] = "M6",
                ["gameExe"] = @"B:\SteamLibrary\Leaf it Alone.exe",
            }.ToJsonString());

            var info = SafeBaseService.ReadFrom(manifest);
            Assert(info.Installed, "installed manifest -> Installed");
            Assert(info.Milestone == "M6", "milestone parsed", info.Milestone);
            Assert(info.GameExe == @"B:\SteamLibrary\Leaf it Alone.exe", "gameExe parsed", info.GameExe ?? "null");

            Assert(!SafeBaseService.ReadFrom(Path.Combine(dir, "absent.json")).Installed,
                "missing manifest -> NotInstalled");
            var bad = Path.Combine(dir, "bad.json");
            File.WriteAllText(bad, "{ not json ");
            Assert(!SafeBaseService.ReadFrom(bad).Installed, "malformed manifest -> NotInstalled (no throw)");

            // -- verdict extraction (pure) --
            var sample = new System.Collections.Generic.List<string>
            {
                "safe-base status", "=====", "  present : yes",
                "VERDICT: safe-base M1 + M2 + M3 + M4 active. Game's outbound blocked.",
            };
            Assert(SafeBaseService.ExtractVerdict(sample)?.StartsWith("safe-base M1 + M2") == true,
                "ExtractVerdict pulls the VERDICT text", SafeBaseService.ExtractVerdict(sample) ?? "null");
            Assert(SafeBaseService.ExtractVerdict(new[] { "no verdict here" }) is null,
                "ExtractVerdict -> null when absent");

            // -- real status.ps1 run through the runner (proves Write-Host capture) --
            File.WriteAllText(Path.Combine(dir, "status.ps1"),
                "$ErrorActionPreference='Stop'\n" +
                "Write-Host 'safe-base status'\n" +
                "Write-Host 'VERDICT: safe-base M1 + M2 + M3 + M4 active.'\nexit 0\n");

            var svc = new SafeBaseService(new ProcessRunner(), dir);
            var (res, verdict) = svc.RunStatusAsync().GetAwaiter().GetResult();
            Assert(res.Success, "fake status.ps1 exited 0", $"exit {res.ExitCode} fault={res.Fault}");
            Assert(verdict == "safe-base M1 + M2 + M3 + M4 active.",
                "VERDICT captured from Write-Host output", verdict ?? "null");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// Log colouring recovers the severity that redirected stdout strips. These cover the
    /// real markers the launcher and the scripts emit, and the priority ordering that must
    /// hold: a failing run is never green, a healthy VERDICT is, and a zero exit is neutral.
    /// </summary>
    private static void CheckLogColorization()
    {
        (string line, LogSeverity want, string what)[] cases =
        {
            ("--- apply OK ---", LogSeverity.Success, "apply OK -> green"),
            ("--- apply FAILED (exit 7) ---", LogSeverity.Error, "apply FAILED -> red"),
            ("applied v1.2.0", LogSeverity.Success, "applied -> green"),
            ("reverted to vanilla", LogSeverity.Success, "reverted -> green"),
            ("VERDICT: safe-base M1 + M2 + M3 + M4 active. Game's outbound blocked.", LogSeverity.Success, "healthy VERDICT -> green"),
            ("VERDICT: not installed. Content mods ... refuse to apply.", LogSeverity.Warning, "not-installed VERDICT -> yellow"),
            ("  MISSING - content mods cannot import the SDK. Re-run install.ps1.", LogSeverity.Error, "MISSING -> red"),
            ("!! a marker has REAPPEARED in the current Saves/", LogSeverity.Error, "'!!' alert -> red"),
            ("[warn] no Python found - apply will use apply.ps1 default.", LogSeverity.Warning, "no Python -> yellow"),
            ("[launcher] exited with code 0.", LogSeverity.Info, "exit 0 -> neutral"),
            ("[launcher] exited with code 5.", LogSeverity.Error, "non-zero exit -> red"),
            ("safe-base status", LogSeverity.Info, "plain header -> neutral"),
            ("python for apply: C:\\Python312\\python.exe", LogSeverity.Info, "path line -> neutral"),
        };
        foreach (var (line, want, what) in cases)
        {
            var got = LogColor.Classify(line);
            Assert(got == want, what, $"got {got} for: {line}");
        }
    }

    /// <summary>
    /// L7 Python detection: file-existence is not enough - the interpreter must be 3.12 AND
    /// import UnityPy. These exercise the pure classifier over synthetic probe output, the
    /// Ready-for-apply mapping (a wrong version with UnityPy still patches), and the
    /// no-interpreter short-circuit that never spawns a process.
    /// </summary>
    private static void CheckPythonHealth()
    {
        PythonHealthResult Classify(int exit, params string[] lines) =>
            PythonHealth.Interpret(new ProcessResult(exit, false), lines);

        var ok = Classify(0, "LEAF_PYVER=3.12.10", "LEAF_UNITYPY=yes");
        Assert(ok.Status == PythonStatus.Ok && ok.ReadyForApply, "3.12 + UnityPy -> Ok, ready", ok.ToString());

        var wrong = Classify(0, "LEAF_PYVER=3.14.0", "LEAF_UNITYPY=yes");
        Assert(wrong.Status == PythonStatus.WrongVersion && wrong.ReadyForApply,
            "other version + UnityPy -> WrongVersion but usable", wrong.ToString());

        var nopy = Classify(0, "LEAF_PYVER=3.12.10", "LEAF_UNITYPY=no");
        Assert(nopy.Status == PythonStatus.MissingUnityPy && !nopy.ReadyForApply,
            "UnityPy not importable -> MissingUnityPy, not ready", nopy.ToString());

        var broken = Classify(0, "some unrelated output");
        Assert(broken.Status == PythonStatus.Broken && !broken.ReadyForApply,
            "no version marker -> Broken", broken.ToString());

        var fault = PythonHealth.Interpret(new ProcessResult(-1, false, "Failed to start"), Array.Empty<string>());
        Assert(fault.Status == PythonStatus.Broken, "spawn fault -> Broken", fault.ToString());

        // No interpreter: ProbeAsync short-circuits without spawning anything.
        var none = PythonHealth.ProbeAsync(new ProcessRunner(), "").GetAwaiter().GetResult();
        Assert(none.Status == PythonStatus.NotInstalled && !none.ReadyForApply,
            "empty path -> NotInstalled (no spawn)", none.ToString());

        // The probe must actually ask for both signals.
        var args = PythonHealth.BuildProbeArgs();
        Assert(args.Count == 2 && args[0] == "-c"
               && args[1].Contains("UnityPy") && args[1].Contains("LEAF_PYVER"),
            "probe args ask for version + UnityPy", string.Join(" ", args));
    }

    /// <summary>
    /// L9 settings store: values round-trip through settings.json, a corrupt file falls
    /// back to defaults (never throws across startup), and a hand-edited file is normalized
    /// (bad theme -> system, bad accent -> house green).
    /// </summary>
    private static void CheckAppConfigRoundTrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leaf-l9cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        try
        {
            var cfg = new AppConfig
            {
                Theme = AppConfig.ThemeDark,
                Accent = "#0E9F9F",
                ContentRootOverride = @"D:\some\root",
                GameExeOverride = @"D:\game\Leaf it Alone.exe",
            };
            cfg.Save(path);

            var back = AppConfig.Load(path);
            Assert(back.Theme == AppConfig.ThemeDark, "theme round-trips (dark)", back.Theme);
            Assert(back.Accent == "#0E9F9F", "accent round-trips", back.Accent);
            Assert(back.ContentRootOverride == @"D:\some\root", "content-root override round-trips", back.ContentRootOverride);
            Assert(back.GameExeOverride == @"D:\game\Leaf it Alone.exe", "game-exe override round-trips", back.GameExeOverride);

            // Corrupt file -> defaults, not a throw.
            File.WriteAllText(path, "{ not json at all ");
            var recovered = AppConfig.Load(path);
            Assert(recovered.Theme == AppConfig.ThemeSystem && recovered.Accent == AppConfig.DefaultAccent,
                "corrupt settings -> defaults", recovered.Theme + "/" + recovered.Accent);

            // Normalization: an invalid theme/accent in an otherwise-good file is clamped.
            File.WriteAllText(path, "{\"theme\":\"NEON\",\"accent\":\"zzz\",\"contentRootOverride\":\"\"}");
            var norm = AppConfig.Load(path);
            Assert(norm.Theme == AppConfig.ThemeSystem, "bad theme normalized to system", norm.Theme);
            Assert(norm.Accent == AppConfig.DefaultAccent, "bad accent normalized to default", norm.Accent);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// L9 content-root override seam: TryLocate honors an explicit start dir (a temp folder
    /// holding content-mods beside a safe-base/tools marker) instead of only walking up, and
    /// a folder without the markers is rejected so a bad override surfaces, not mis-locates.
    /// </summary>
    private static void CheckContentRootOverride()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leaf-l9root-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(dir, "modset");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "content-mods"));
            Directory.CreateDirectory(Path.Combine(root, "safe-base"));
            Directory.CreateDirectory(Path.Combine(dir, "empty"));

            var located = new RepoLocator();
            Assert(located.TryLocate(root), "explicit override root with markers is located", located.LastError ?? "");
            Assert(string.Equals(located.Root, root, StringComparison.OrdinalIgnoreCase),
                "override root resolved to the given dir", located.Root ?? "null");
            Assert(located.ModsRoot is not null && Directory.Exists(located.ModsRoot), "override ModsRoot exists", located.ModsRoot ?? "null");

            var none = new RepoLocator();
            Assert(!none.TryLocate(Path.Combine(dir, "empty")), "a folder without markers is rejected");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// L9 game-exe override: an override naming a real file wins; an empty or stale override
    /// falls back to the manifest resolution (the same value the no-arg call returns), so a
    /// deleted override never blocks launching.
    /// </summary>
    private static void CheckGameExeOverride()
    {
        var dir = Path.Combine(Path.GetTempPath(), "leaf-l9exe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var realExe = Path.Combine(dir, "Leaf it Alone.exe");
            File.WriteAllText(realExe, "stub");

            Assert(Equals(GameLauncher.ResolveGameExe(realExe), realExe),
                "existing override exe wins", GameLauncher.ResolveGameExe(realExe) ?? "null");

            string baseline = GameLauncher.ResolveGameExe() ?? "(null)";
            Assert(Equals(GameLauncher.ResolveGameExe("") ?? "(null)", baseline),
                "empty override falls back to manifest", GameLauncher.ResolveGameExe("") ?? "null");

            var stale = Path.Combine(dir, "gone.exe");
            Assert(Equals(GameLauncher.ResolveGameExe(stale) ?? "(null)", baseline),
                "stale (missing) override falls back to manifest", GameLauncher.ResolveGameExe(stale) ?? "null");
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    /// <summary>
    /// L9 theme helpers (pure): hex parsing, the lighten/darken accent shades, the readable
    /// on-accent foreground, and system->light/dark resolution. These drive the live swap.
    /// </summary>
    private static void CheckThemeMath()
    {
        var green = ThemeManager.ParseHex("#3FA34D");
        Assert(green.R == 0x3F && green.G == 0xA3 && green.B == 0x4D, "ParseHex reads #3FA34D", $"{green.R},{green.G},{green.B}");

        var bad = ThemeManager.ParseHex("nonsense");
        Assert(bad == (Color)ColorConverter.ConvertFromString(AppConfig.DefaultAccent)!,
            "ParseHex falls back to default on junk", bad.ToString());

        var lighter = ThemeManager.Lighten(green, 0.5);
        Assert(lighter.G > green.G && lighter.G <= 255, "Lighten moves toward white", lighter.ToString());
        var darker = ThemeManager.Darken(green, 0.5);
        Assert(darker.G < green.G, "Darken moves toward black", darker.ToString());

        Assert(ThemeManager.OnAccent(green) == Colors.White, "white text on the green accent");
        Assert(ThemeManager.OnAccent(Colors.Yellow) != Colors.White, "dark text on a bright (yellow) accent");
        Assert(ThemeManager.OnAccent(Colors.Black) == Colors.White, "white text on black");

        Assert(ThemeManager.ResolveEffective(AppConfig.ThemeLight) == AppConfig.ThemeLight, "light resolves to light");
        Assert(ThemeManager.ResolveEffective(AppConfig.ThemeDark) == AppConfig.ThemeDark, "dark resolves to dark");
        var sys = ThemeManager.ResolveEffective(AppConfig.ThemeSystem);
        Assert(sys is AppConfig.ThemeLight or AppConfig.ThemeDark, "system resolves to a concrete theme", sys);
    }

    /// <summary>
    /// The whole live-swap relies on Light.xaml and Dark.xaml defining the SAME brush keys
    /// (only the values differ). A missing key would silently drop a color after a swap, so
    /// assert the key sets match by scanning the palette source files.
    /// </summary>
    private static void CheckThemeTokenParity()
    {
        var themeDir = FindThemeDir();
        if (themeDir is null)
        {
            Assert(false, "located the Theme/ folder for parity check", "not found from " + AppContext.BaseDirectory);
            return;
        }

        var light = ExtractKeys(Path.Combine(themeDir, "Light.xaml"));
        var dark = ExtractKeys(Path.Combine(themeDir, "Dark.xaml"));

        Assert(light.Count > 0 && dark.Count > 0, "both palettes define keys", $"light={light.Count} dark={dark.Count}");
        var onlyLight = light.Except(dark).ToList();
        var onlyDark = dark.Except(light).ToList();
        Assert(onlyLight.Count == 0 && onlyDark.Count == 0, "Light and Dark define the same brush keys",
            $"onlyLight=[{string.Join(",", onlyLight)}] onlyDark=[{string.Join(",", onlyDark)}]");

        // The accent + text + surface keys the styles rely on must actually be present.
        foreach (var required in new[] { "AccentBrush", "WindowBackgroundBrush", "SurfaceBrush", "TextPrimaryBrush", "ConsoleBgBrush" })
            Assert(light.Contains(required), $"light palette defines {required}", "");
    }

    private static System.Collections.Generic.HashSet<string> ExtractKeys(string xamlPath)
    {
        var set = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(xamlPath)) return set;
        foreach (Match m in Regex.Matches(File.ReadAllText(xamlPath), "x:Key=\"([^\"]+)\""))
            set.Add(m.Groups[1].Value);
        return set;
    }

    /// <summary>Walk up from the test binary to the Theme/ source folder (compiled dicts are not loose files).</summary>
    private static string? FindThemeDir()
    {
        // The Theme/ folder is a source tree under src/Launcher/Theme, which is a sibling
        // of tests/ - not an ancestor of the test binary. So at each ancestor we probe the
        // known source layouts relative to it, not just a direct Theme/ child.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var rel in new[]
                     {
                 "Theme",
                 Path.Combine("src", "Launcher", "Theme"),
                 Path.Combine("launcher", "src", "Launcher", "Theme"),
                     })
            {
                var candidate = Path.Combine(dir.FullName, rel);
                if (File.Exists(Path.Combine(candidate, "Light.xaml"))) return candidate;
            }
            dir = dir.Parent;
        }
        return null;
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
