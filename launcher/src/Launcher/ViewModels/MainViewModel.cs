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
        ApplyCommand = new RelayCommand(async _ => await ApplyAsync(), _ => CanApply);
        RevertCommand = new RelayCommand(async _ => await RevertAsync(), _ => CanRevert);

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
                RefreshCommands();
            }
        }
    }

    public RelayCommand RescanCommand { get; }
    public RelayCommand DemoRunnerCommand { get; }
    public RelayCommand ApplyCommand { get; }
    public RelayCommand RevertCommand { get; }

    // Apply is offered when the selection has something to push to the game; Revert
    // only when it is currently applied. All gated on not being mid-operation.
    private bool CanApply => !IsBusy && SelectedEditor is { IsPending: true };
    private bool CanRevert => !IsBusy && SelectedEditor is { IsApplied: true };

    private void RefreshCommands()
    {
        ApplyCommand.RefreshCanExecute();
        RevertCommand.RefreshCanExecute();
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
    /// Drive <c>apply.ps1</c> for the selected mod. Mirrors the script's cheap
    /// preconditions first (game closed / safe-base installed / Python present) for an
    /// instant message, persists any edits, streams the engine log, and on a clean
    /// exit records the applied sha + version so the badge clears. A non-zero exit or
    /// timeout means the script bailed before committing - the game is left untouched.
    /// </summary>
    private async System.Threading.Tasks.Task ApplyAsync()
    {
        var ed = SelectedEditor;
        if (ed is null) return;
        var mod = ed.Mod;

        var block = _ops.BlockingReason(mod, forRevert: false);
        if (block is not null)
        {
            StatusText = block;
            Log($"[apply blocked] {mod.Id}: {block}");
            return;
        }

        IsBusy = true;
        try
        {
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
                StatusText = $"{mod.Name} applied.";
                Log("--- apply OK ---");
            }
            else
            {
                StatusText = res.TimedOut
                    ? $"{mod.Name}: apply timed out (game untouched)."
                    : $"{mod.Name}: apply failed (exit {res.ExitCode}); game left untouched.";
                Log(res.TimedOut ? "--- apply TIMEOUT ---" : $"--- apply FAILED (exit {res.ExitCode}) ---");
            }
        }
        finally { IsBusy = false; }
    }

    /// <summary>
    /// Drive <c>revert.ps1</c> for the selected mod: restore the pristine vanilla
    /// bundle through the safe-base and clear the recorded applied sha + version.
    /// </summary>
    private async System.Threading.Tasks.Task RevertAsync()
    {
        var ed = SelectedEditor;
        if (ed is null) return;
        var mod = ed.Mod;

        var block = _ops.BlockingReason(mod, forRevert: true);
        if (block is not null)
        {
            StatusText = block;
            Log($"[revert blocked] {mod.Id}: {block}");
            return;
        }

        IsBusy = true;
        try
        {
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
                StatusText = $"{mod.Name} reverted.";
                Log("--- revert OK ---");
            }
            else
            {
                StatusText = res.TimedOut
                    ? $"{mod.Name}: revert timed out."
                    : $"{mod.Name}: revert failed (exit {res.ExitCode}).";
                Log(res.TimedOut ? "--- revert TIMEOUT ---" : $"--- revert FAILED (exit {res.ExitCode}) ---");
            }
        }
        finally { IsBusy = false; }
    }

    private string PythonOrDefault() => string.IsNullOrEmpty(_ops.PythonPath) ? "apply.ps1 default" : _ops.PythonPath!;

    private void RaisePendingCount() => OnPropertyChanged(nameof(PendingCount));

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
