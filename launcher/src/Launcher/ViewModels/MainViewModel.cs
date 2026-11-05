using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using LeafItAlone.Launcher.Models;
using LeafItAlone.Launcher.Services;

namespace LeafItAlone.Launcher.ViewModels;

/// <summary>
/// Backing VM for <c>MainWindow</c>. L1 scope: locate the content root, discover
/// mods from their manifests, show them read-only, and prove the subprocess
/// runner streams into the log drawer. Config editing (L2) and apply orchestration
/// (L3+) hang off this same VM later.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly RepoLocator _locator = new();
    private readonly ModDiscovery _discovery = new();
    private readonly ProcessRunner _runner = new();

    public MainViewModel()
    {
        RescanCommand = new RelayCommand(_ => Rescan(), _ => !IsBusy);
        DemoRunnerCommand = new RelayCommand(async _ => await DemoRunnerAsync(), _ => !IsBusy);

        _runner.LineReceived += AppendLog;   // fires on a worker thread; marshalled in AppendLog

        Rescan();
    }

    public ObservableCollection<ModInfo> Mods { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    private ModInfo? _selected;
    public ModInfo? SelectedMod { get => _selected; set => Set(ref _selected, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _rootPath = "";
    public string RootPath { get => _rootPath; set => Set(ref _rootPath, value); }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (Set(ref _isBusy, value)) { RescanCommand.RefreshCanExecute(); DemoRunnerCommand.RefreshCanExecute(); } }
    }

    public RelayCommand RescanCommand { get; }
    public RelayCommand DemoRunnerCommand { get; }

    /// <summary>Populate the read-only mod list straight from the manifests on disk.</summary>
    public void Rescan()
    {
        Log("Scanning for mods...");
        if (!_locator.TryLocate())
        {
            Mods.Clear();
            RootPath = "";
            StatusText = "Content root not found.";
            Log(_locator.LastError ?? "unknown locator error");
            return;
        }

        RootPath = _locator.Root!;
        var result = _discovery.Discover(_locator.ModsRoot!);

        Mods.Clear();
        foreach (var m in result.Mods) Mods.Add(m);

        foreach (var err in result.Errors) Log("[manifest error] " + err);

        StatusText = Mods.Count == 0
            ? $"No mod manifests found under {result.Mods.Count} scanned at {RootPath}"
            : $"{Mods.Count} mod(s) discovered at {RootPath}";
        SelectedMod ??= Mods.FirstOrDefault();
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
