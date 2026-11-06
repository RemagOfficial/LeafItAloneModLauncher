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
    private readonly CompositeModDiscovery _compositeDiscovery = new();
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

    // ---- Mod-sources (2a): shared plumbing wired once per Rescan() ----
    private SourceRegistry _sources = SourceRegistry.CreateDefault();
    private string _sourcesPath = "";
    private GitHubApi? _gitApi;
    private GitHubSourceService? _gitSource;
    private RemoteModStager? _stager;

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

        // Mod-sources (2a R4/R5): commands wired to async implementations below.
        RefreshSourcesCommand = new RelayCommand(async _ => await RefreshSourcesAsync(), _ => !IsBusy && !BrowseBusy);
        AddSourceCommand = new RelayCommand(async _ => await AddSourceAsync(), _ => !IsBusy && !string.IsNullOrWhiteSpace(NewSourceUrl) && _config.AllowNonDefaultSources);
        RemoveSourceCommand = new RelayCommand(async _ => await RemoveSourceAsync(), _ => !IsBusy && SelectedSource is not null);
        InstallRemoteCommand = new RelayCommand(async _ => await InstallRemoteAsync(), _ => !IsBusy && SelectedBrowse is not null && !BrowseBusy);
        OpenRemoteModsFolderCommand = new RelayCommand(_ => OpenRemoteModsFolder());
        SetModsPageModeCommand = new RelayCommand(p => { if (p is string s) ModsPageMode = s; });

        // 2b: self-update / diagnostics commands.
        OpenUpdatePageCommand = new RelayCommand(_ => OpenUrl(UpdateUrl), _ => UpdateAvailable && !string.IsNullOrWhiteSpace(UpdateUrl));
        ExportDiagnosticsCommand = new RelayCommand(async _ => await ExportDiagnosticsAsync(), _ => !IsBusy);

        _runner.LineReceived += AppendLog;   // fires on a worker thread; marshalled in AppendLog

        // Load the app's own preferences before the first scan: the theme is already
        // applied by App.OnStartup, but the content-root override must be in place so
        // Rescan() can honour it, and the Settings page binds to these values.
        _configPath = AppConfig.DefaultPath();
        _config = AppConfig.Load(_configPath);
        _sourcesPath = SourceRegistry.DefaultPath();
        _sources = SourceRegistry.Load(_sourcesPath);
        RebuildSourcesList();

        Rescan();

        // 2b: kick off the non-critical network services (self-update check +
        // first-run What's new) without blocking startup. Launched from the UI
        // thread, so its awaits resume there (no ConfigureAwait(false)) and every
        // property/binding update stays on the dispatcher.
        _ = RunStartupServicesAsync();
    }



    public ObservableCollection<ConfigEditorViewModel> Editors { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    /// <summary>
    /// 2b conflict detection: human-readable warnings for engine values that two or
    /// more *enabled* mods both rewrite, recomputed whenever the enable set changes.
    /// Empty when nothing collides (the normal case for the shipped mods). Bound to a
    /// warning card on the Mods page.
    /// </summary>
    public ObservableCollection<string> ConflictWarnings { get; } = new();

    /// <summary>True when <see cref="ConflictWarnings"/> has anything to show (drives the card's visibility).</summary>
    public bool HasConflicts => ConflictWarnings.Count > 0;

    // ---- Mod-sources (2a R4): data-driven UI surfaces for the Browse + Settings tabs. ----
    public ObservableCollection<SourceItem> Sources { get; } = new();
    public ObservableCollection<BrowseItem> BrowseItems { get; } = new();
    private bool _browseBusy;
    public bool BrowseBusy { get => _browseBusy; set => Set(ref _browseBusy, value); }
    private string _browseStatus = "no source fetched yet";
    public string BrowseStatus { get => _browseStatus; set => Set(ref _browseStatus, value); }
    private string _newSourceUrl = "";
    public string NewSourceUrl { get => _newSourceUrl; set => Set(ref _newSourceUrl, value); }
    private string _newSourceRef = "";
    public string NewSourceRef { get => _newSourceRef; set => Set(ref _newSourceRef, value); }
    private SourceItem? _selectedSource;
    public SourceItem? SelectedSource { get => _selectedSource; set => Set(ref _selectedSource, value); }
    private BrowseItem? _selectedBrowse;
    public BrowseItem? SelectedBrowse
    {
        get => _selectedBrowse;
        set
        {
            if (Set(ref _selectedBrowse, value))
            {
                // CommandManager.RequerySuggested covers user-driven clicks, but
                // a programmatic setter call (e.g. right after a Refresh leaves
                // only one row selectable) needs an explicit poke so the
                // "Install / Update selected" button lights up immediately.
                InstallRemoteCommand?.RefreshCanExecute();
            }
        }
    }

    public RelayCommand RefreshSourcesCommand { get; private set; } = null!;
    public RelayCommand AddSourceCommand { get; private set; } = null!;
    public RelayCommand RemoveSourceCommand { get; private set; } = null!;
    public RelayCommand InstallRemoteCommand { get; private set; } = null!;
    public RelayCommand OpenRemoteModsFolderCommand { get; private set; } = null!;

    // 2b: self-update download link + diagnostics bundle.
    public RelayCommand OpenUpdatePageCommand { get; private set; } = null!;
    public RelayCommand ExportDiagnosticsCommand { get; private set; } = null!;

    /// <summary>The app version shown in Settings / about (from the assembly).</summary>
    public string AppVersion => AppInfo.Version;

    private bool _updateAvailable;
    /// <summary>True when GitHub's latest release is newer than the installed version.</summary>
    public bool UpdateAvailable { get => _updateAvailable; private set => Set(ref _updateAvailable, value); }

    private string _updateVersionText = "";
    /// <summary>e.g. "Update available: 1.1.0" (empty when up to date / unknown).</summary>
    public string UpdateVersionText { get => _updateVersionText; private set => Set(ref _updateVersionText, value); }

    private string _updateUrl = "";
    /// <summary>The release page to open when the user clicks the update chip.</summary>
    public string UpdateUrl { get => _updateUrl; private set => Set(ref _updateUrl, value); }

    /// <summary>
    /// 2b: raised once per app version to show the "What's new" panel. The View owns
    /// the window (same seam pattern as <see cref="RequestTrustApproval"/>); null in
    /// tests, in which case the panel is simply not shown but the version is still
    /// marked seen so it never nags.
    /// </summary>
    public System.Action<string /*title*/, string /*markdown body*/>? RequestWhatsNew { get; set; }

    /// <summary>
    /// Raised when the trust modal (R5) needs to run before an install proceeds.
    /// The View owns the dialog and returns whether the user typed INSTALL.
    /// Null = no gate (used in tests); a null handler is treated as "user approved".
    /// </summary>
    public System.Func<ModSource, RemoteModEntry, System.Threading.Tasks.Task<bool>>? RequestTrustApproval { get; set; }

    /// <summary>True when the current AppConfig lets the user add non-default sources.</summary>
    public bool AllowNonDefaultSources => _config.AllowNonDefaultSources;

    public string AllowNonDefaultSourcesToggle
    {
        get => _config.AllowNonDefaultSources ? "true" : "false";
        set
        {
            var on = string.Equals(value, "true", System.StringComparison.OrdinalIgnoreCase);
            if (_config.AllowNonDefaultSources == on) return;
            _config.AllowNonDefaultSources = on;
            SaveConfig();
            OnPropertyChanged(nameof(AllowNonDefaultSources));
            OnPropertyChanged(nameof(AllowNonDefaultSourcesToggle));
            Log($"[settings] allow non-default sources -> {(on ? "ON" : "OFF")}");
            RefreshSourcesCommand.RefreshCanExecute();
        }
    }

    /// <summary>
    /// 2b: whether the output console is expanded. Bound two-way to the console
    /// toggle. Persists so the window reopens the way the user left it; defaults to
    /// collapsed for a clean first impression.
    /// </summary>
    public bool ConsoleExpanded
    {
        get => _config.ConsoleExpanded;
        set
        {
            if (_config.ConsoleExpanded == value) return;
            _config.ConsoleExpanded = value;
            SaveConfig();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 2b: window size. Restored from <c>settings.json</c> on startup and written
    /// back once on close (not per resize tick). The View reads these in its ctor and
    /// calls <see cref="SaveWindowSize"/> from <c>OnClosing</c>.
    /// </summary>
    public double WindowWidth => _config.WindowWidth;
    public double WindowHeight => _config.WindowHeight;

    /// <summary>2b: persist the window's current size; floored to the minimum by AppConfig.Normalize.</summary>
    public void SaveWindowSize(double width, double height)
    {
        if (System.Math.Abs(_config.WindowWidth - width) < 1 && System.Math.Abs(_config.WindowHeight - height) < 1)
            return;
        _config.WindowWidth = width;
        _config.WindowHeight = height;
        SaveConfig();
    }

    /// <summary>The rate-limit line shown under the Browse tab (updated after each fetch).</summary>
    private string _gitHubRateLine = "";
    public string GitHubRateLine { get => _gitHubRateLine; private set => Set(ref _gitHubRateLine, value); }

    /// <summary>Which Mods-page sub-panel is visible: "installed" (default) or "browse".</summary>
    private string _modsPageMode = "installed";
    public string ModsPageMode { get => _modsPageMode; set { if (Set(ref _modsPageMode, value)) { OnPropertyChanged(nameof(ShowInstalledPanel)); OnPropertyChanged(nameof(ShowBrowsePanel)); } } }
    public bool ShowInstalledPanel => _modsPageMode == "installed";
    public bool ShowBrowsePanel => _modsPageMode == "browse";
    public RelayCommand SetModsPageModeCommand { get; private set; } = null!;

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
            ConflictWarnings.Clear();
            OnPropertyChanged(nameof(HasConflicts));
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

        // Mod-sources (2a): load the source list next to state.json so Browse/Install
        // has its data ready before the first UI refresh. The GitHub services are
        // created lazily - only when the user actually hits Browse or Apply on a
        // remote mod do we need them - but the stager wiring happens here so
        // ApplyOneAsync always sees a non-null hook.
        _sourcesPath = SourceRegistry.DefaultPath();
        _sources = SourceRegistry.Load(_sourcesPath);
        _gitApi ??= new GitHubApi();
        _gitSource ??= new GitHubSourceService(_gitApi);
        _stager ??= new RemoteModStager(_gitApi, _gitSource);
        _ops.EnsurePresentHook = EnsureRemotePresentAsync;

        // The engine folder (safe-base/ or tools/) holds install/status/uninstall-all.
        _safeBase = new SafeBaseService(_runner, _locator.SafeBaseDir);
        RefreshSafeBaseState();
        RaiseGameExeDisplay();

        // Discovery now merges the built-in content-mods/ with every installed remote
        // mod under remote-mods/<sourceKey>/. We feed it the (key,url,path) triples so
        // the wrapped ModInfo carries the source URL for the Details pane.
        var remoteRoots = BuildRemoteTriples();
        var result = _compositeDiscovery.Discover(_locator.ModsRoot!, remoteRoots);
        foreach (var err in result.Errors) Log("[manifest error] " + err);

        Editors.Clear();
        foreach (var mod in result.Mods)
        {
            // Fill SourceSha from state so the badge can show "installed at abc123"
            // without hitting the network. Composite discovery has no state access.
            var st = _state.For(mod.Id);
            var displayMod = mod;
            if (mod.IsRemote && st.Remote is not null && string.IsNullOrEmpty(mod.SourceSha))
            {
                displayMod = new ModInfo(mod.Manifest, mod.Folder, mod.ManifestPath)
                {
                    SourceKey = mod.SourceKey,
                    SourceUrl = string.IsNullOrEmpty(mod.SourceUrl) ? st.Remote.SourceUrl : mod.SourceUrl,
                    SourceSha = st.Remote.Sha,
                };
            }
            var editor = new ConfigEditorViewModel(displayMod, Log);
            editor.ApplyState(st);
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
        RecomputeConflicts();
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
            RecomputeConflicts();
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
    /// 2b: re-run <see cref="ConflictAnalyzer"/> over the currently-enabled mods and
    /// refresh <see cref="ConflictWarnings"/>. Cheap (in-memory manifest reads), so it
    /// runs on every enable toggle and after a rescan. Logs newly-appeared warnings
    /// once so a risky combine is visible in the console too, not just the card.
    /// </summary>
    private void RecomputeConflicts()
    {
        var enabled = Editors.Where(e => e.Enabled).Select(e => e.Mod);
        var conflicts = ConflictAnalyzer.Analyze(enabled);

        var before = ConflictWarnings.Select(w => w).ToHashSet(StringComparer.Ordinal);
        ConflictWarnings.Clear();
        foreach (var c in conflicts)
        {
            ConflictWarnings.Add(c.Message);
            if (!before.Contains(c.Message)) Log("[conflict] " + c.Message);
        }
        OnPropertyChanged(nameof(HasConflicts));
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
            // 2b: make a risky combine explicit right before touching the game.
            RecomputeConflicts();
            foreach (var w in ConflictWarnings) Log("[conflict] " + w);

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

    // ---- Mod-sources (2a R4/R5): command bodies ----

    private void RebuildSourcesList()
    {
        Sources.Clear();
        foreach (var s in _sources.Sources) Sources.Add(new SourceItem(s));
    }

    /// <summary>
    /// Fetch every enabled source's mod listing and rebuild <see cref="BrowseItems"/>.
    /// Sequential to keep the request rate small; sources that fail show a per-row
    /// error rather than aborting the whole sweep.
    /// </summary>
    private async System.Threading.Tasks.Task RefreshSourcesAsync()
    {
        if (_gitSource is null || _gitApi is null) { BrowseStatus = "network stack not initialised"; return; }
        BrowseBusy = true;
        BrowseItems.Clear();
        var totalMods = 0;
        try
        {
            var builtInIds = new System.Collections.Generic.HashSet<string>(
                Editors.Where(e => !e.Mod.IsRemote).Select(e => e.Mod.Id),
                System.StringComparer.OrdinalIgnoreCase);
            foreach (var src in Sources)
            {
                if (!src.Source.Enabled) continue;
                Log($"[sources] refreshing {src.Source.Key} @ {src.Source.Url}");
                try
                {
                    // No ConfigureAwait(false): the continuations touch UI-affine
                    // state (BrowseItems ObservableCollection, RelayCommand.CanExecute-
                    // Changed, SourceItem.Refresh which raises PropertyChanged that
                    // WPF bindings listen to). Staying on the captured WPF
                    // SynchronizationContext keeps all of that on the UI thread.
                    var snap = await _gitSource.FetchListingsAsync(src.Source);
                    src.Source.LastFetchedUtc = snap.FetchedUtc.ToString("o");
                    src.Source.LastSha = snap.Sha;
                    src.Refresh();
                    foreach (var err in snap.Errors) Log("[sources]   " + err);
                    foreach (var entry in snap.Mods)
                    {
                        var installed = _state.For(entry.Id).Remote;
                        var sameSha = installed is not null
                                      && string.Equals(installed.SourceKey, snap.SourceKey, System.StringComparison.OrdinalIgnoreCase)
                                      && string.Equals(installed.Sha, snap.Sha, System.StringComparison.OrdinalIgnoreCase);
                        var update = installed is not null && !sameSha;
                        BrowseItems.Add(new BrowseItem
                        {
                            SourceKey = snap.SourceKey,
                            SourceUrl = src.Source.Url,
                            SourceShortSha = Short(snap.Sha),
                            Entry = entry,
                            CollidesWithBuiltIn = builtInIds.Contains(entry.Id),
                            InstalledAtSameSha = sameSha,
                            UpdateAvailable = update,
                        });
                        totalMods++;
                    }
                }
                catch (System.Exception ex)
                {
                    Log("[sources]   refresh failed: " + ex.Message);
                }
            }
            try { _sources.Save(_sourcesPath); }
            catch (System.Exception ex) { Log("[sources] save failed: " + ex.Message); }
            var remaining = _gitApi.LastRateLimit.Remaining;
            GitHubRateLine = remaining > 0 ? $"GitHub API: {remaining} req/hr remaining" : "";
            BrowseStatus = $"{BrowseItems.Count} mod(s) available across {Sources.Count} source(s) ({totalMods} fetched)";
        }
        finally
        {
            BrowseBusy = false;
            RefreshSourcesCommand.RefreshCanExecute();
            InstallRemoteCommand.RefreshCanExecute();
        }
    }

    private async System.Threading.Tasks.Task AddSourceAsync()
    {
        if (!_config.AllowNonDefaultSources)
        {
            Log("[sources] refused: 'Allow non-default sources' is OFF in Settings");
            return;
        }
        if (!ModSource.TryParseOwnerRepo(NewSourceUrl, out _, out _))
        {
            Log($"[sources] not a GitHub repo URL: {NewSourceUrl}");
            return;
        }
        var s = new ModSource
        {
            Key = ModSource.DeriveKey(NewSourceUrl),
            Url = NewSourceUrl.Trim(),
            RefName = (NewSourceRef ?? "").Trim(),
            Enabled = true,
            Trusted = false,   // R5: prompt for INSTALL on first download
        };
        _sources.Sources.Add(s);
        _sources.Normalize();
        try { _sources.Save(_sourcesPath); }
        catch (System.Exception ex) { Log("[sources] save failed: " + ex.Message); return; }
        RebuildSourcesList();
        NewSourceUrl = ""; NewSourceRef = "";
        Log($"[sources] added {s.Key}");
        await RefreshSourcesAsync();
    }

    private async System.Threading.Tasks.Task RemoveSourceAsync()
    {
        var sel = SelectedSource;
        if (sel is null) return;
        _sources.Sources.RemoveAll(x => string.Equals(x.Key, sel.Source.Key, System.StringComparison.OrdinalIgnoreCase));
        try { _sources.Save(_sourcesPath); }
        catch (System.Exception ex) { Log("[sources] save failed: " + ex.Message); return; }
        RebuildSourcesList();
        Log($"[sources] removed {sel.Source.Key}. Installed files stay under remote-mods/{sel.Source.Key}/ and mods already enabled keep working.");
        await System.Threading.Tasks.Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task InstallRemoteAsync()
    {
        var row = SelectedBrowse;
        if (row is null || _stager is null) return;
        var src = _sources.FindByKey(row.SourceKey);
        if (src is null) { Log("[install] source not registered: " + row.SourceKey); return; }
        if (row.HasManifestError) { Log($"[install] refused: {row.Entry.ManifestError}"); return; }
        if (row.CollidesWithBuiltIn) { Log($"[install] refused: '{row.Id}' is a built-in mod"); return; }

        // R5: trust gate for untrusted (third-party) sources.
        if (!src.Trusted && RequestTrustApproval is not null)
        {
            var ok = await RequestTrustApproval(src, row.Entry);
            if (!ok) { Log("[install] cancelled at trust prompt"); return; }
            src.Trusted = true;
            _sources.Save(_sourcesPath);
            RebuildSourcesList();
        }

        BrowseBusy = true;
        try
        {
            Log($"[install] fetching {row.Id} from {src.Url}@{row.SourceShortSha}");
            var snap = await _gitSource!.FetchListingsAsync(src);
            var entry = snap.Mods.FirstOrDefault(m => string.Equals(m.Id, row.Id, System.StringComparison.OrdinalIgnoreCase));
            if (entry is null) { Log("[install] mod disappeared from the source listing"); return; }
            var builtInIds = Editors.Where(e => !e.Mod.IsRemote).Select(e => e.Mod.Id);
            var dest = RepoLocator.RemoteModFolder(src.Key, row.Id);
            var res = await _stager.InstallAsync(src, snap, entry, _state, builtInIds, _statePath);
            Log("[install] " + res.Message);
            if (res.Ok)
            {
                // Push source's SHA forward so the badge clears on the next refresh.
                src.LastSha = snap.Sha;
                src.LastFetchedUtc = snap.FetchedUtc.ToString("o");
                _sources.Save(_sourcesPath);
                RebuildSourcesList();
                Rescan();
            }
        }
        catch (System.Exception ex) { Log("[install] error: " + ex.Message); }
        finally { BrowseBusy = false; }
    }

    private static void OpenRemoteModsFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = RepoLocator.RemoteModsRoot,
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex) { Console.WriteLine("open folder failed: " + ex.Message); }
    }

    /// <summary>
    /// 2b: the two non-critical startup services, run after the first scan so they
    /// never delay the window. Started from the UI thread and awaited WITHOUT
    /// ConfigureAwait(false), so the update properties and the What's-new seam all
    /// land back on the dispatcher. Failures are logged, never thrown.
    /// </summary>
    private async System.Threading.Tasks.Task RunStartupServicesAsync()
    {
        try
        {
            var res = await new UpdateChecker().CheckAsync();
            if (res.Known && res.UpdateAvailable)
            {
                UpdateAvailable = true;
                UpdateVersionText = "Update available: " + res.LatestVersion;
                UpdateUrl = res.Url;
                OpenUpdatePageCommand.RefreshCanExecute();
                Log("[update] a newer release is available: " + res.LatestVersion);
            }
            else if (res.Known)
            {
                Log($"[update] up to date (v{AppInfo.Version})");
            }
            else
            {
                Log("[update] check skipped (offline, or no releases published yet)");
            }
        }
        catch (System.Exception ex) { Log("[update] check failed: " + ex.Message); }

        await MaybeShowWhatsNewAsync();
    }

    /// <summary>
    /// 2b: show the "What's new" panel the first time a given app version runs. Marks
    /// the version seen once notes are retrieved (or the tag simply has no release),
    /// so it never repeats; a network failure leaves it unseen to retry next launch
    /// without ever spamming the user on the same successful run.
    /// </summary>
    private async System.Threading.Tasks.Task MaybeShowWhatsNewAsync()
    {
        var current = AppInfo.Version;
        if (string.Equals(_config.LastWhatsNewVersion, current, System.StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            using var api = new GitHubApi();
            var rel = await api.GetReleaseByTagAsync(AppInfo.Owner, AppInfo.Repo, AppInfo.TagFor(current));
            if (rel is not null && !string.IsNullOrWhiteSpace(rel.Body))
                RequestWhatsNew?.Invoke("What's new in " + current, rel.Body);
            // Whether or not there were notes, this version has now been handled.
            _config.LastWhatsNewVersion = current;
            SaveConfig();
        }
        catch (System.Exception ex)
        {
            Log("[whatsnew] could not load release notes (will retry): " + ex.Message);
        }
    }

    /// <summary>Open https URL / local path in the default handler (shell execute).</summary>
    private static void OpenUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex) { Console.WriteLine("open url failed: " + ex.Message); }
    }

    /// <summary>
    /// 2b: build the diagnostics zip (settings + state + configs + remote manifests +
    /// a generated about.txt, never any game file) and open the folder it landed in.
    /// </summary>
    private async System.Threading.Tasks.Task ExportDiagnosticsAsync()
    {
        var launcherDir = System.IO.Path.GetDirectoryName(_configPath) ?? ".";
        var safeRoot = System.IO.Path.GetDirectoryName(launcherDir) ?? launcherDir;
        try
        {
            var about = BuildAboutText();
            // The zip write is quick but we keep the UI honest by yielding first.
            var zipPath = await System.Threading.Tasks.Task.Run(() => DiagnosticsExporter.Export(safeRoot, about));
            Log("[diagnostics] wrote " + zipPath);
            OpenRemotePath(zipPath);
        }
        catch (System.Exception ex)
        {
            Log("[diagnostics] export failed: " + ex.Message);
        }
    }

    private string BuildAboutText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Leaf it Alone Mod Launcher - diagnostics");
        sb.AppendLine("generated: " + System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        sb.AppendLine("app version: " + AppInfo.Version);
        sb.AppendLine("os: " + System.Environment.OSVersion.VersionString + " (" + (System.Environment.Is64BitOperatingSystem ? "x64" : "x86") + ")");
        sb.AppendLine("content root: " + (_locator.Root ?? "(not located)"));
        sb.AppendLine("engine root: " + (_locator.EngineRoot ?? "(not located)"));
        sb.AppendLine("safe-base: " + (_safeBaseInfo.Installed ? $"installed ({_safeBaseInfo.Milestone})" : "NOT installed"));
        sb.AppendLine("python: " + PythonStatusText);
        sb.AppendLine();
        sb.AppendLine("mod sources:");
        foreach (var s in Sources)
            sb.AppendLine($"  - {s.Source.Url}  [{s.Source.RefName}]  lastSha={(s.Source.LastSha ?? "")}  trusted={s.Source.Trusted}");
        sb.AppendLine();
        sb.AppendLine("recent log:");
        foreach (var line in System.Linq.Enumerable.Take(LogLines, 500))
            sb.AppendLine("  " + line);
        return sb.ToString();
    }

    /// <summary>Reveal a just-written file in its folder (shell execute on the parent).</summary>
    private static void OpenRemotePath(string fullPath)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.IO.Path.GetDirectoryName(fullPath) ?? ".",
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex) { Console.WriteLine("open folder failed: " + ex.Message); }
    }

    private static string Short(string sha) => string.IsNullOrEmpty(sha) ? "" : sha[..System.Math.Min(7, sha.Length)];

    /// <summary>
    /// Mod-sources (2a): enumerate every source that currently has an installed mod
    /// under <c>remote-mods/&lt;key&gt;/</c> so CompositeModDiscovery can pick up the tree
    /// with proper URLs. Sources with no installed folder are skipped here (they
    /// still appear in the Browse tab, just not in the Mods list).
    /// </summary>
    private System.Collections.Generic.IEnumerable<(string SourceKey, string Url, string Path)> BuildRemoteTriples()
    {
        var root = RepoLocator.RemoteModsRoot;
        if (!System.IO.Directory.Exists(root))
            return System.Array.Empty<(string, string, string)>();
        var list = new System.Collections.Generic.List<(string, string, string)>();
        foreach (var dir in System.IO.Directory.EnumerateDirectories(root))
        {
            var key = System.IO.Path.GetFileName(dir);
            var src = _sources.FindByKey(key);
            var url = src?.Url ?? "";
            list.Add((key, url, dir));
        }
        return list;
    }

    /// <summary>
    /// Hook called by <see cref="ApplyOrchestrator"/> before running apply.ps1. Returns
    /// Ok=true for built-in mods (nothing to heal). For remote mods it consults the
    /// recorded per-file SHA manifest and re-downloads anything the user removed or
    /// that drifted; a hard failure aborts the apply with a readable message.
    /// </summary>
    private async System.Threading.Tasks.Task<(bool Ok, string Message)> EnsureRemotePresentAsync(ModInfo mod)
    {
        if (!mod.IsRemote) return (true, "");
        if (_stager is null) return (false, "remote mod stager not initialised (internal error)");
        try
        {
            var res = await _stager.EnsurePresentAsync(mod, _state, _sources, _statePath,
                progress: new Progress<string>(Log));
            if (!res.Ok) return (false, res.Message);
            if (res.FilesWritten > 0) Log("[heal] " + res.Message);
            return (true, res.Message);
        }
        catch (Exception ex)
        {
            return (false, "ensure-present failed: " + ex.Message);
        }
    }

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
