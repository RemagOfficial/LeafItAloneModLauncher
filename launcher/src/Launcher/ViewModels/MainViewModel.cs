using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using LeafItAlone.Launcher.Models;
using LeafItAlone.Launcher.Services;

namespace LeafItAlone.Launcher.ViewModels;

/// <summary>
/// Backing VM for <c>MainWindow</c>. L2 scope: locate the content root, discover
/// mods from their manifests, and build a schema-driven <see cref="ConfigEditorViewModel"/>
/// for each (list shows a live "pending" badge; the detail pane is the editor).
/// Apply orchestration (L3), launch (L4) and the safe-base lifecycle (L5) hang off
/// this same VM later.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly RepoLocator _locator = new();
    private readonly ModDiscovery _discovery = new();
    private readonly ProcessRunner _runner = new();
    private LauncherState _state = new();
    private string _statePath = "";

    public MainViewModel()
    {
        RescanCommand = new RelayCommand(_ => Rescan(), _ => !IsBusy);
        DemoRunnerCommand = new RelayCommand(async _ => await DemoRunnerAsync(), _ => !IsBusy);

        _runner.LineReceived += AppendLog;   // fires on a worker thread; marshalled in AppendLog

        Rescan();
    }

    public ObservableCollection<ConfigEditorViewModel> Editors { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private ConfigEditorViewModel? _selectedEditor;
    public ConfigEditorViewModel? SelectedEditor
    {
        get => _selectedEditor;
        set => Set(ref _selectedEditor, value);
    }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _rootPath = "";
    public string RootPath { get => _rootPath; set => Set(ref _rootPath, value); }

    public int PendingCount => Editors.Count(e => e.IsPending);

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) { RescanCommand.RefreshCanExecute(); DemoRunnerCommand.RefreshCanExecute(); } }
    }

    public RelayCommand RescanCommand { get; }
    public RelayCommand DemoRunnerCommand { get; }

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

        StatusText = Editors.Count == 0
            ? $"No mod manifests found at {RootPath}"
            : $"{Editors.Count} mod(s) at {RootPath}";
        RaisePendingCount();
    }

    /// <summary>Persist the enable toggle to state.json the moment it changes.</summary>
    private void OnEditorPropertyChanged(ConfigEditorViewModel editor, string? name)
    {
        if (name == nameof(ConfigEditorViewModel.Enabled))
        {
            _state.For(editor.Mod.Id).Enabled = editor.Enabled;
            try { _state.Save(_statePath); }
            catch (Exception ex) { Log("state save failed: " + ex.Message); }
            RaisePendingCount();
        }
        else if (name == nameof(ConfigEditorViewModel.IsPending))
        {
            RaisePendingCount();
        }
    }

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
