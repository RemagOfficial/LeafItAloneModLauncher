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
    private SafeBaseService? _safeBase;
    private SafeBaseInfo _safeBaseInfo = SafeBaseInfo.NotInstalled;
    private string _safeBaseVerdict = "";
    private PythonHealthResult _python = new(PythonStatus.Unknown, "", "");
    private bool _pythonPromptShown;
    private LauncherState _state = new();
    private string _statePath = "";
    private AppConfig _config = new();
    private string _configPath = "";

    public MainViewModel()
    {
        _ops = new ApplyOrchestrator(_runner);

        RescanCommand = new RelayCommand(_ => Rescan(), _ => !IsBusy);
        DemoRunnerCommand = new RelayCommand(async _ => await DemoRunnerAsync(), _ => !IsBusy);
        ApplyCommand = new RelayCommand(async _ => await ApplySelectedAsync(), _ => CanApply);
        RevertCommand = new RelayCommand(async _ => await RevertSelectedAsync(), _ => CanRevert);
        ApplyAndLaunchCommand = new RelayCommand(async _ => await ApplyAndLaunchAsync(), _ => !IsBusy && Editors.Count > 0);
        LaunchCommand = new RelayCommand(_ => LaunchGame(), _ => !IsBusy);
        InstallSafeBaseCommand = new RelayCommand(async _ => await InstallSafeBaseAsync(), _ => CanInstallSafeBase);
        UninstallAllCommand = new RelayCommand(async _ => await UninstallAllAsync(), _ => CanUninstallAll);
        RefreshStatusCommand = new RelayCommand(async _ => await RefreshStatusAsync(), _ => !IsBusy && _safeBase is not null);
        PythonSetupCommand = new RelayCommand(_ => RequestPythonSetup?.Invoke(this, EventArgs.Empty));

        // L9: nav + settings commands.
        SelectSectionCommand = new RelayCommand(p => SelectSection(p));
        BrowseContentRootCommand = new RelayCommand(_ => RequestBrowseContentRoot?.Invoke(this, EventArgs.Empty));
        BrowseGameExeCommand = new RelayCommand(_ => RequestBrowseGameExe?.Invoke(this, EventArgs.Empty));
        ClearContentRootCommand = new RelayCommand(_ => { ContentRootOverride = ""; });
        ClearGameExeCommand = new RelayCommand(_ => { GameExeOverride = ""; });
        ResetSettingsCommand = new RelayCommand(_ => ResetSettings());
        SelectThemeCommand = new RelayCommand(p => { if (p is string s) ThemeChoice = s; });
        SelectAccentCommand = new RelayCommand(p => { if (p is string s) AccentChoice = s; });
        ClearLogCommand = new RelayCommand(_ => { LogLines.Clear(); Log("output cleared"); });

        _runner.LineReceived += AppendLog;   // fires on a worker thread; marshalled in AppendLog

        // Load the app's own preferences before the first scan: the theme is already
        // applied by App.OnStartup, but the content-root override must be in place so
        // Rescan() can honour it, and the Settings page binds to these values.
        _configPath = AppConfig.DefaultPath();
        _config = AppConfig.Load(_configPath);

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

    public int PendingCount => Editors.Count(e => e.IsPending);

    /// <summary>Drives the header's pending chip (a count badge only shows when non-zero).</summary>
    public bool HasPending => PendingCount > 0;

    /// <summary>One-line summary the Dashboard shows on the Apply &amp; Launch button area.</summary>
    public string ChangeSummary => PendingCount == 0
        ? "no changes to apply - Launch starts the game as-is"
        : $"{PendingCount} mod(s) pending - Apply & Launch pushes them, then starts the game";

    // ---- L5: safe-base lifecycle ----

    /// <summary>True once the safe-base manifest exists (its install marker).</summary>
    public bool SafeBaseInstalled => _safeBaseInfo.Installed;

    /// <summary>True when nothing is installed yet - the Dashboard's first-run prompt.</summary>
    public bool FirstRunNeeded => !_safeBaseInfo.Installed;

    /// <summary>What the Dashboard shows about the framework's install state.</summary>
    public string SafeBaseSummary => _safeBaseInfo.Installed
        ? $"Installed ({_safeBaseInfo.Milestone}) - game: {ShortExe(_safeBaseInfo.GameExe)}"
        : "Not installed - the safe-base must be installed before mods can apply.";

    /// <summary>The one-line VERDICT from the last full status run (empty until you run it).</summary>
    public string SafeBaseVerdict
    {
        get => _safeBaseVerdict;
        private set => Set(ref _safeBaseVerdict, value);
    }

    private static string ShortExe(string? exe) =>
        string.IsNullOrWhiteSpace(exe) ? "(unknown)" : System.IO.Path.GetFileName(exe!);

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
                InstallSafeBaseCommand.RefreshCanExecute();
                UninstallAllCommand.RefreshCanExecute();
                RefreshStatusCommand.RefreshCanExecute();
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
    public RelayCommand InstallSafeBaseCommand { get; }
    public RelayCommand UninstallAllCommand { get; }
    public RelayCommand RefreshStatusCommand { get; }
    public RelayCommand PythonSetupCommand { get; }

    // ---- L9: navigation + app settings ----

    public RelayCommand SelectSectionCommand { get; }
    public RelayCommand BrowseContentRootCommand { get; }
    public RelayCommand BrowseGameExeCommand { get; }
    public RelayCommand ClearContentRootCommand { get; }
    public RelayCommand ClearGameExeCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }
    public RelayCommand SelectThemeCommand { get; }
    public RelayCommand SelectAccentCommand { get; }
    public RelayCommand ClearLogCommand { get; }

    /// <summary>Raised when the Settings page wants a folder picker (the View owns the dialog).</summary>
    public event EventHandler? RequestBrowseContentRoot;
    /// <summary>Raised when the Settings page wants a file picker for the game exe.</summary>
    public event EventHandler? RequestBrowseGameExe;

    private Section _section = Section.Dashboard;

    /// <summary>Which page the nav rail shows. Setting it refreshes the Show* flags + title.</summary>
    public Section SelectedSection
    {
        get => _section;
        set
        {
            if (!Set(ref _section, value)) return;
            OnPropertyChanged(nameof(ShowDashboard));
            OnPropertyChanged(nameof(ShowMods));
            OnPropertyChanged(nameof(ShowSettings));
            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(IsDashboardChecked));
            OnPropertyChanged(nameof(IsModsChecked));
            OnPropertyChanged(nameof(IsSettingsChecked));
        }
    }

    private void SelectSection(object? parameter)
    {
        if (parameter is Section s) SelectedSection = s;
        else if (parameter is string str && Enum.TryParse(str, out Section parsed)) SelectedSection = parsed;
    }

    // Page visibility flags (bound to the three views) + nav-checked flags.
    public bool ShowDashboard => _section == Section.Dashboard;
    public bool ShowMods => _section == Section.Mods;
    public bool ShowSettings => _section == Section.Settings;
    public bool IsDashboardChecked => _section == Section.Dashboard;
    public bool IsModsChecked => _section == Section.Mods;
    public bool IsSettingsChecked => _section == Section.Settings;

    public string PageTitle => _section switch
    {
        Section.Mods => "Mods",
        Section.Settings => "Settings",
        _ => "Dashboard",
    };

    /// <summary>Theme token (light/dark/system). Changing it re-applies the palette live and persists.</summary>
    public string ThemeChoice
    {
        get => _config.Theme;
        set
        {
            if (_config.Theme == value) return;
            _config.Theme = value;
            ApplyAndSaveTheme();
            OnPropertyChanged();
        }
    }

    /// <summary>Accent as #RRGGBB. Changing it re-shades the accent live and persists.</summary>
    public string AccentChoice
    {
        get => _config.Accent;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !IsHex(value)) return;   // ignore partial typing
            value = NormalizeHex(value);
            if (_config.Accent == value) return;
            _config.Accent = value;
            ApplyAndSaveTheme();
            OnPropertyChanged();
        }
    }

    /// <summary>Content root the launcher was pointed at (empty = auto-detect). Editing re-scans.</summary>
    public string ContentRootOverride
    {
        get => _config.ContentRootOverride;
        set
        {
            value ??= "";
            if (_config.ContentRootOverride == value) return;
            _config.ContentRootOverride = value;
            SaveConfig();
            Log(string.IsNullOrEmpty(value)
                ? "[settings] content root: auto-detect"
                : $"[settings] content root override -> {value}");
            Rescan();
            OnPropertyChanged();
        }
    }

    /// <summary>Game exe the launcher should start (empty = safe-base manifest). Editing persists.</summary>
    public string GameExeOverride
    {
        get => _config.GameExeOverride;
        set
        {
            value ??= "";
            if (_config.GameExeOverride == value) return;
            _config.GameExeOverride = value;
            SaveConfig();
            Log(string.IsNullOrEmpty(value)
                ? "[settings] game exe: from safe-base manifest"
                : $"[settings] game exe override -> {value}");
            RaiseGameExeDisplay();
            OnPropertyChanged();
        }
    }

    /// <summary>The manifest-detected game exe (or empty), for the Settings page's "current" line.</summary>
    public string DetectedGameExe => GameLauncher.ResolveGameExe() ?? "";

    /// <summary>What Launch will actually run: the override if set + present, else the manifest exe.</summary>
    public string GameExeDisplay =>
        !string.IsNullOrWhiteSpace(_config.GameExeOverride) && System.IO.File.Exists(_config.GameExeOverride)
            ? _config.GameExeOverride
            : (DetectedGameExe);

    public bool HasContentRootOverride => !string.IsNullOrWhiteSpace(_config.ContentRootOverride);
    public bool HasGameExeOverride => !string.IsNullOrWhiteSpace(_config.GameExeOverride);

    private void ApplyAndSaveTheme()
    {
        ThemeManager.Apply(_config.Theme, _config.Accent);
        SaveConfig();
        OnPropertyChanged(nameof(AccentChoice));
    }

    private void ResetSettings()
    {
        _config.Theme = AppConfig.ThemeSystem;
        _config.Accent = AppConfig.DefaultAccent;
        _config.ContentRootOverride = "";
        _config.GameExeOverride = "";
        SaveConfig();
        ThemeManager.Apply(_config.Theme, _config.Accent);
        OnPropertyChanged(nameof(ThemeChoice));
        OnPropertyChanged(nameof(AccentChoice));
        OnPropertyChanged(nameof(ContentRootOverride));
        OnPropertyChanged(nameof(GameExeOverride));
        RaiseOverrideFlags();
        RaiseGameExeDisplay();
        Log("[settings] reset to defaults");
        Rescan();
    }

    private void SaveConfig()
    {
        try { _config.Save(_configPath); }
        catch (Exception ex) { Log("settings save failed: " + ex.Message); }
        RaiseOverrideFlags();
    }

    private void RaiseOverrideFlags()
    {
        OnPropertyChanged(nameof(HasContentRootOverride));
        OnPropertyChanged(nameof(HasGameExeOverride));
    }

    private void RaiseGameExeDisplay()
    {
        OnPropertyChanged(nameof(DetectedGameExe));
        OnPropertyChanged(nameof(GameExeDisplay));
    }

    private static bool IsHex(string s) =>
        s.Length == 7 && s[0] == '#' &&
        System.Text.RegularExpressions.Regex.IsMatch(s[1..], "^[0-9a-fA-F]{6}$");

    private static string NormalizeHex(string s) => s.Trim().ToUpperInvariant();


    // ---- L7: Python runtime detection + guided setup ----

    /// <summary>
    /// Raised when the launcher wants the Setup guide shown - automatically the first time a
    /// probe finds no usable Python, and on demand from the Dashboard button / popup re-check.
    /// The View subscribes and opens the dialog on the UI thread, keeping the VM engine-agnostic.
    /// </summary>
    public event EventHandler? RequestPythonSetup;

    /// <summary>Interpreter handed to <c>apply.ps1 -Python</c> (empty until resolved).</summary>
    public string PythonPath => _ops.PythonPath ?? "";

    /// <summary>Latest validated state of the interpreter (exists AND imports UnityPy).</summary>
    public PythonStatus PythonState => _python.Status;

    /// <summary>True when the interpreter can drive apply.ps1 (UnityPy present, any version).</summary>
    public bool IsPythonReady => _python.ReadyForApply;

    /// <summary>True once we know the runtime is not ready - shows the Setup call-to-action.</summary>
    public bool PythonNeedsSetup => _python.Status is not (PythonStatus.Unknown or PythonStatus.Ok);

    /// <summary>One-line description of the current Python state for the Dashboard.</summary>
    public string PythonStatusText => PythonHealth.Describe(_python);

    /// <summary>The pinned interpreter download page, surfaced in the popup.</summary>
    public string PythonDownloadUrl => PythonHealth.DownloadUrl;

    /// <summary>The UnityPy install command, surfaced (and copyable) in the popup.</summary>
    public string PythonPipCommand => PythonHealth.PipCommand;

    // Apply is offered when the selection has something to push to the game; Revert
    // only when it is currently applied. All gated on not being mid-operation.
    private bool CanApply => !IsBusy && SelectedEditor is { IsPending: true };
    private bool CanRevert => !IsBusy && SelectedEditor is { IsApplied: true };

    // Install only when the base is absent; Uninstall only when present; both need the
    // engine folder resolved and the game closed (the scripts also enforce these).
    private bool CanInstallSafeBase => !IsBusy && _safeBase is not null && !_safeBaseInfo.Installed;
    private bool CanUninstallAll => !IsBusy && _safeBase is not null && _safeBaseInfo.Installed;

    private void RefreshCommands()
    {
        ApplyCommand.RefreshCanExecute();
        RevertCommand.RefreshCanExecute();
        ApplyAndLaunchCommand.RefreshCanExecute();
        LaunchCommand.RefreshCanExecute();
        InstallSafeBaseCommand.RefreshCanExecute();
        UninstallAllCommand.RefreshCanExecute();
        RefreshStatusCommand.RefreshCanExecute();
    }


    /// <summary>Rebuild the editor set straight from the manifests + live configs on disk.</summary>
    public void Rescan()
    {
        Log("Scanning for mods...");
        var start = string.IsNullOrWhiteSpace(_config.ContentRootOverride) ? null : _config.ContentRootOverride;
        if (!_locator.TryLocate(start))
        {
            Editors.Clear();
            RootPath = "";
            _safeBase = null;
            _safeBaseInfo = SafeBaseInfo.NotInstalled;
            RaiseSafeBaseProps();
            StatusText = "Content root not found.";
            Log(_locator.LastError ?? "unknown locator error");
            return;
        }

        RootPath = _locator.Root!;
        _statePath = LauncherState.DefaultPath();
        _state = LauncherState.Load(_statePath);

        // The engine folder (safe-base/ or tools/) holds install/status/uninstall-all.
        _safeBase = new SafeBaseService(_runner, _locator.SafeBaseDir);
        RefreshSafeBaseState();
        RaiseGameExeDisplay();

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

        // Locate the interpreter for apply.ps1, then validate it asynchronously (L7):
        // "does it exist AND import UnityPy". We do not embed a Python, so a not-ready
        // result raises RequestPythonSetup once to guide the user to install it.
        _ops.PythonPath = PythonLocator.Resolve(_locator.Root);
        OnPropertyChanged(nameof(PythonPath));
        _ = ProbePythonAsync();

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
        var (ok, message) = GameLauncher.Launch(_config.GameExeOverride);
        StatusText = ok ? "Game launched. Play safe (offline)." : message;
        Log("[launch] " + message);
    }


    // ---- L5: safe-base lifecycle flows ----

    /// <summary>Install the safe-base (self-elevates). Blocks if the game is running.</summary>
    private async System.Threading.Tasks.Task InstallSafeBaseAsync()
    {
        if (_safeBase is null) { StatusText = "Engine folder not found - cannot install."; return; }
        if (_safeBaseInfo.Installed) { StatusText = "safe-base is already installed."; RefreshSafeBaseState(); return; }
        if (ApplyOrchestrator.IsGameRunning())
        {
            StatusText = "Close the game before installing safe-base.";
            Log("[install blocked] Leaf it Alone is running - close it first");
            return;
        }

        IsBusy = true;
        try
        {
            Log("--- install safe-base (a UAC prompt will appear) ---");
            var res = await _safeBase.InstallAsync();
            if (res.Success) { Log("--- install OK ---"); StatusText = "safe-base installed."; }
            else Log(res.TimedOut ? "--- install TIMEOUT ---" : $"--- install FAILED (exit {res.ExitCode}) ---");
        }
        finally { IsBusy = false; RefreshSafeBaseState(); }
    }

    /// <summary>Revert every registered mod, then uninstall the safe-base (self-elevates).</summary>
    private async System.Threading.Tasks.Task UninstallAllAsync()
    {
        if (_safeBase is null) { StatusText = "Engine folder not found - cannot uninstall."; return; }
        if (!_safeBaseInfo.Installed) { StatusText = "safe-base is not installed."; RefreshSafeBaseState(); return; }
        if (ApplyOrchestrator.IsGameRunning())
        {
            StatusText = "Close the game before uninstalling safe-base.";
            Log("[uninstall blocked] Leaf it Alone is running - close it first");
            return;
        }

        IsBusy = true;
        try
        {
            Log("--- uninstall-all: revert mods, remove safe-base (a UAC prompt will appear) ---");
            var res = await _safeBase.UninstallAllAsync();
            if (res.Success)
            {
                Log("--- uninstall-all OK ---");
                StatusText = "safe-base removed; all mods reverted.";
                ClearAllApplied();   // the base is gone - nothing stays "applied" in our state
            }
            else Log(res.TimedOut ? "--- uninstall TIMEOUT ---" : $"--- uninstall FAILED (exit {res.ExitCode}) ---");
        }
        finally { IsBusy = false; Rescan(); }   // rebuild editors + safe-base state from disk
    }

    /// <summary>Run status.ps1 for the authoritative health verdict (streams full report to the log).</summary>
    private async System.Threading.Tasks.Task RefreshStatusAsync()
    {
        if (_safeBase is null) return;
        IsBusy = true;
        try
        {
            Log("--- safe-base status ---");
            var (res, verdict) = await _safeBase.RunStatusAsync();
            SafeBaseVerdict = verdict ?? "(no verdict line found)";
            StatusText = res.Success ? "status refreshed" : $"status run exited {res.ExitCode}";
            Log("--- status end ---");
        }
        finally { IsBusy = false; RefreshSafeBaseState(); }
    }

    /// <summary>Re-read the install manifest and notify the Dashboard bindings.</summary>
    private void RefreshSafeBaseState()
    {
        _safeBaseInfo = _safeBase is null ? SafeBaseInfo.NotInstalled : _safeBase.Read();
        RaiseSafeBaseProps();
        RaiseGameExeDisplay();
    }

    private void RaiseSafeBaseProps()
    {
        OnPropertyChanged(nameof(SafeBaseInstalled));
        OnPropertyChanged(nameof(FirstRunNeeded));
        OnPropertyChanged(nameof(SafeBaseSummary));
        InstallSafeBaseCommand.RefreshCanExecute();
        UninstallAllCommand.RefreshCanExecute();
        RefreshStatusCommand.RefreshCanExecute();
    }

    /// <summary>Drop every mod's applied marker (used after the whole base is uninstalled).</summary>
    private void ClearAllApplied()
    {
        foreach (var st in _state.Mods.Values)
        {
            st.LastAppliedConfigSha = "";
            st.AppliedVersion = "";
        }
        TrySaveState();
    }

    private string PythonOrDefault() => string.IsNullOrEmpty(_ops.PythonPath) ? "apply.ps1 default" : _ops.PythonPath!;

    /// <summary>
    /// Validate the resolved interpreter off the UI thread (one cheap <c>python -c</c> probe).
    /// The result is applied back on the dispatcher so the bound Dashboard + popup are happy.
    /// </summary>
    private async System.Threading.Tasks.Task ProbePythonAsync()
    {
        var result = await PythonHealth.ProbeAsync(_runner, _ops.PythonPath);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            // Fire-and-forget on purpose: the probe runs off the UI thread and posts its
            // result back. Discard the DispatcherOperation so the awaitable isn't ignored.
            _ = dispatcher.BeginInvoke(new Action(() => ApplyPythonResult(result)));
            return;
        }
        ApplyPythonResult(result);
    }

    private void ApplyPythonResult(PythonHealthResult result)
    {
        _python = result;
        _ops.PythonReady = result.ReadyForApply;
        OnPropertyChanged(nameof(PythonState));
        OnPropertyChanged(nameof(IsPythonReady));
        OnPropertyChanged(nameof(PythonNeedsSetup));
        OnPropertyChanged(nameof(PythonStatusText));
        OnPropertyChanged(nameof(PythonPath));
        Log("[python] " + PythonHealth.Describe(result));

        // Guide the user the first time we discover there is no usable runtime (L7).
        if (!result.ReadyForApply && !_pythonPromptShown)
        {
            _pythonPromptShown = true;
            RequestPythonSetup?.Invoke(this, EventArgs.Empty);
        }
        RefreshCommands();
    }

    /// <summary>Re-run the probe on demand (the popup's "Re-check"). Clears the once-guard.</summary>
    public void RecheckPython()
    {
        _pythonPromptShown = false;
        _ = ProbePythonAsync();
    }

    private void RaisePendingCount()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasPending));
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
