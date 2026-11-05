using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using LeafItAlone.Launcher.Models;
using LeafItAlone.Launcher.Services;

namespace LeafItAlone.Launcher.ViewModels;

/// <summary>
/// Backing VM for <c>MainWindow</c>. Discovers mods from their manifests and builds
/// a schema-driven <see cref="ConfigEditorViewModel"/> for each. L3 adds apply/revert
/// orchestration: the selected mod's Apply/Revert buttons drive the real
/// <c>apply.ps1</c>/<c>revert.ps1</c> through <see cref="ApplyOrchestrator"/> +
/// <see cref="ProcessRunner"/>, mirror the scripts' preconditions for a friendly
/// early message, stream the engine's output to the log, and on success record the
/// applied sha + version in state.json so the pending badge clears. Launch (L4) and
/// the safe-base lifecycle (L5) hang off this same VM later.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly RepoLocator _locator = new();
    private readonly ModDiscovery _discovery = new();
    private readonly ProcessRunner _runner = new();
    private readonly ApplyOrchestrator _ops;
    private LauncherState _state = new();
    private string _statePath = "";

    public MainViewModel()
    {
        _ops = new ApplyOrchestrator(_runner);

        RescanCommand = new RelayCommand(_ => Rescan(), _ => !IsBusy);
        DemoRunnerCommand = new RelayCommand(async _ => await DemoRunnerAsync(), _ => !IsBusy);
        ApplyCommand = new RelayCommand(async _ => await ApplySelectedAsync(), _ => CanApply);
        RevertCommand = new RelayCommand(async _ => await RevertSelectedAsync(), _ => CanRevert);
        ApplyAndLaunchCommand = new RelayCommand(async _ => await ApplyAndLaunchAsync(), _ => !IsBusy && Editors.Count > 0);
        LaunchCommand = new RelayCommand(_ => LaunchGame(), _ => !IsBusy);

        _runner.LineReceived += AppendLog;   // fires on a worker thread; marshalled in AppendLog

        Rescan();
    }

    public ObservableCollection<ConfigEditorViewModel> Editors { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private ConfigEditorViewModel? _selectedEditor;
    public ConfigEditorViewModel? SelectedEditor
    {
        get => _selectedEditor;
        set { if (Set(ref _selectedEditor, value)) RefreshCommands(); }
    }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _rootPath = "";
    public string RootPath { get => _rootPath; set => Set(ref _rootPath, value); }

    /// <summary>Interpreter handed to <c>apply.ps1 -Python</c> (empty until resolved).</summary>
    public string PythonPath => _ops.PythonPath ?? "";

    public int PendingCount => Editors.Count(e => e.IsPending);

    /// <summary>One-line summary the Dashboard shows on the Apply &amp; Launch button area.</summary>
    public string ChangeSummary => PendingCount == 0
        ? "no changes to apply - Launch starts the game as-is"
        : $"{PendingCount} mod(s) pending - Apply & Launch pushes them, then starts the game";

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
                RescanCommand.RefreshCanExecute();
                DemoRunnerCommand.RefreshCanExecute();
                ApplyAndLaunchCommand.RefreshCanExecute();
                LaunchCommand.RefreshCanExecute();
                RefreshCommands();
            }
        }
    }

    public RelayCommand RescanCommand { get; }
    public RelayCommand DemoRunnerCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand RevertCommand { get; }
    public RelayCommand ApplyAndLaunchCommand { get; }
    public RelayCommand LaunchCommand { get; }

    // Apply is offered when the selection has something to push to the game; Revert
    // only when it is currently applied. All gated on not being mid-operation.
    private bool CanApply => !IsBusy && SelectedEditor is { IsPending: true };
    private bool CanRevert => !IsBusy && SelectedEditor is { IsApplied: true };

    private void RefreshCommands()
    {
        ApplyCommand.RefreshCanExecute();
        RevertCommand.RefreshCanExecute();
        ApplyAndLaunchCommand.RefreshCanExecute();
        LaunchCommand.RefreshCanExecute();
    }


    /// <summary>Rebuild the editor set straight from the manifests + live configs on disk.</summary>
    public void Rescan()
    {
        Log("Scanning for mods...");
        if (!_locator.TryLocate())
        {
            Editors.Clear();
            RootPath = "";
            StatusText = "Content root not found.";
            Log(_locator.LastError ?? "unknown locator error");
            return;
        }

        RootPath = _locator.Root!;
        _statePath = LauncherState.DefaultPath();
        _state = LauncherState.Load(_statePath);

        var result = _discovery.Discover(_locator.ModsRoot!);
        foreach (var err in result.Errors) Log("[manifest error] " + err);

        Editors.Clear();
        foreach (var mod in result.Mods)
        {
            var editor = new ConfigEditorViewModel(mod, Log);
            editor.ApplyState(_state.For(mod.Id));
            editor.PropertyChanged += (_, e) => OnEditorPropertyChanged(editor, e.PropertyName);
            Editors.Add(editor);
        }
        SelectedEditor = Editors.FirstOrDefault();

        // Resolve the interpreter for apply.ps1 now that the root is known (L7 will
        // ship a bundled runtime/py; until then this finds the system Python).
        _ops.PythonPath = PythonLocator.Resolve(_locator.Root);
        OnPropertyChanged(nameof(PythonPath));
        if (string.IsNullOrEmpty(_ops.PythonPath))
            Log("[warn] no Python found - apply will use apply.ps1's own default, or set LEAF_PYTHON.");
        else
            Log($"python for apply: {_ops.PythonPath}");

        StatusText = Editors.Count == 0
            ? $"No mod manifests found at {RootPath}"
            : $"{Editors.Count} mod(s) at {RootPath}";
        RaisePendingCount();
        RefreshCommands();
    }

    /// <summary>Persist the enable toggle to state.json the moment it changes.</summary>
    private void OnEditorPropertyChanged(ConfigEditorViewModel editor, string? name)
    {
        if (name == nameof(ConfigEditorViewModel.Enabled))
        {
            _state.For(editor.Mod.Id).Enabled = editor.Enabled;
            TrySaveState();
            RaisePendingCount();
            if (editor == SelectedEditor) RefreshCommands();
        }
        else if (name == nameof(ConfigEditorViewModel.IsPending))
        {
            RaisePendingCount();
            if (editor == SelectedEditor) RefreshCommands();
        }
        else if (name == nameof(ConfigEditorViewModel.IsApplied))
        {
            if (editor == SelectedEditor) RefreshCommands();
        }
    }

    private void TrySaveState()
    {
        try { _state.Save(_statePath); }
        catch (Exception ex) { Log("state save failed: " + ex.Message); }
    }

    /// <summary>
    /// Apply one mod through the engine (no UI concerns): mirror the script's cheap
    /// preconditions, push any edits to disk, run <c>apply.ps1</c>, and on a clean exit
    /// record the applied sha + version + enabled in state.json so the badge clears.
    /// Returns true on success; a block / non-zero exit / timeout means the game was
    /// left untouched. Shared by the per-mod Apply button and the L4 "Apply &amp; Launch"
    /// sequence.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> ApplyOneAsync(ConfigEditorViewModel ed)
    {
        var mod = ed.Mod;
        var block = _ops.BlockingReason(mod, forRevert: false);
        if (block is not null) { Log($"[apply blocked] {mod.Id}: {block}"); StatusText = block; return false; }

        ed.Save();   // push any edits to disk before the engine reads the config
        Log($"--- apply {mod.Id} v{mod.Version} (python: {PythonOrDefault()}) ---");
        var res = await _ops.ApplyAsync(mod);
        if (res.Success)
        {
            var st = _state.For(mod.Id);
            st.Enabled = true;
            st.LastAppliedConfigSha = ed.CurrentConfigSha;
            st.AppliedVersion = mod.Version;
            TrySaveState();
            ed.MarkApplied(ed.CurrentConfigSha);
            ed.Enabled = true;
            ed.Info = $"applied v{mod.Version}";
            Log("--- apply OK ---");
            return true;
        }
        Log(res.TimedOut ? "--- apply TIMEOUT ---" : $"--- apply FAILED (exit {res.ExitCode}) ---");
        return false;
    }

    /// <summary>
    /// Revert one mod through <c>revert.ps1</c> (restore the pristine vanilla bundle via
    /// the safe-base) and clear the recorded applied sha + version. Returns success.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> RevertOneAsync(ConfigEditorViewModel ed)
    {
        var mod = ed.Mod;
        var block = _ops.BlockingReason(mod, forRevert: true);
        if (block is not null) { Log($"[revert blocked] {mod.Id}: {block}"); StatusText = block; return false; }

        Log($"--- revert {mod.Id} ---");
        var res = await _ops.RevertAsync(mod);
        if (res.Success)
        {
            var st = _state.For(mod.Id);
            st.Enabled = false;
            st.LastAppliedConfigSha = "";
            st.AppliedVersion = "";
            TrySaveState();
            ed.MarkReverted();
            ed.Enabled = false;
            ed.Info = "reverted to vanilla";
            Log("--- revert OK ---");
            return true;
        }
        Log(res.TimedOut ? "--- revert TIMEOUT ---" : $"--- revert FAILED (exit {res.ExitCode}) ---");
        return false;
    }

    // ---- per-mod buttons: run the core for the selected mod + a status line ----

    private async System.Threading.Tasks.Task ApplySelectedAsync()
    {
        var ed = SelectedEditor;
        if (ed is null) return;
        IsBusy = true;
        try { StatusText = await ApplyOneAsync(ed) ? $"{ed.Mod.Name} applied." : $"{ed.Mod.Name}: apply failed - game left untouched."; }
        finally { IsBusy = false; }
    }

    private async System.Threading.Tasks.Task RevertSelectedAsync()
    {
        var ed = SelectedEditor;
        if (ed is null) return;
        IsBusy = true;
        try { StatusText = await RevertOneAsync(ed) ? $"{ed.Mod.Name} reverted." : $"{ed.Mod.Name}: revert failed (non-zero exit)."; }
        finally { IsBusy = false; }
    }

    // ---- L4: Apply & Launch ----

    /// <summary>
    /// The Dashboard's one action: reconcile every mod against its enable toggle in
    /// manifest <c>order</c> (lowest first), then start the game. An enabled mod whose
    /// config differs from what is applied gets applied; a disabled mod that is still
    /// applied gets reverted; anything already in sync is skipped. Any blocked or failed
    /// operation aborts BEFORE launching, so the game never starts with a half-applied,
    /// inconsistent mod set. With nothing pending it simply launches.
    /// </summary>
    private async System.Threading.Tasks.Task ApplyAndLaunchAsync()
    {
        IsBusy = true;
        try
        {
            int changes = 0;
            foreach (var ed in Editors.OrderBy(e => e.Mod.Order))
            {
                switch (ApplyOrchestrator.Plan(ed.Enabled, ed.IsApplied, ed.IsPending))
                {
                    case ModOperation.Apply:
                        Log($"[seq] apply {ed.Mod.Id} (order {ed.Mod.Order})");
                        if (!await ApplyOneAsync(ed)) { StatusText = $"stopped: {ed.Mod.Name} failed to apply - game not launched."; return; }
                        changes++;
                        break;
                    case ModOperation.Revert:
                        Log($"[seq] revert {ed.Mod.Id} (order {ed.Mod.Order})");
                        if (!await RevertOneAsync(ed)) { StatusText = $"stopped: {ed.Mod.Name} failed to revert - game not launched."; return; }
                        changes++;
                        break;
                }
            }

            Log(changes == 0 ? "[seq] nothing to apply - launching as-is" : $"[seq] {changes} mod(s) reconciled, launching");
            LaunchGame();
        }
        finally { IsBusy = false; }
    }

    /// <summary>Start the game from the path safe-base recorded in its manifest.</summary>
    private void LaunchGame()
    {
        if (ApplyOrchestrator.IsGameRunning())
        {
            StatusText = "Leaf it Alone is already running.";
            Log("[launch] game already running - not starting another instance");
            return;
        }
        var (ok, message) = GameLauncher.Launch();
        StatusText = ok ? "Game launched. Play safe (offline)." : message;
        Log("[launch] " + message);
    }


    private string PythonOrDefault() => string.IsNullOrEmpty(_ops.PythonPath) ? "apply.ps1 default" : _ops.PythonPath!;

    private void RaisePendingCount()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(ChangeSummary));
    }

    /// <summary>
    /// L1 verification that the subprocess plumbing works end to end: run a trivial
    /// PowerShell one-liner through the real runner and watch it stream + report an
    /// exit code, without touching the game or the safe-base.
    /// </summary>
    private async System.Threading.Tasks.Task DemoRunnerAsync()
    {
        IsBusy = true;
        try
        {
            Log("--- runner demo: powershell -NoProfile -Command (Get-Date) ---");
            var ps = Environment.GetFolderPath(Environment.SpecialFolder.System) + "\\WindowsPowerShell\\v1.0\\powershell.exe";
            var res = await _runner.RunAsync(
                ps,
                new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", "'leaf-launcher runner ok ' + (Get-Date -Format o)" },
                timeout: TimeSpan.FromSeconds(20));
            Log(res.Success ? "--- runner demo OK ---" : $"--- runner demo failed (exit {res.ExitCode}, timeout={res.TimedOut}) ---");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Log(string line) => AppendLog(line);

    /// <summary>Thread-safe append; runner events arrive on background threads.</summary>
    private void AppendLog(string line)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => AppendLog(line)));
            return;
        }

        LogLines.Add(line);
        while (LogLines.Count > 500) LogLines.RemoveAt(0);   // bound the drawer
    }
}
