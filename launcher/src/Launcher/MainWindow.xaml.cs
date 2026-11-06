using System.Linq;
using System.Windows;
using System.Threading.Tasks;
using LeafItAlone.Launcher.Services;
using LeafItAlone.Launcher.ViewModels;
using LeafItAlone.Launcher.Views;

namespace LeafItAlone.Launcher;

/// <summary>
/// Shell window. All state lives in <see cref="MainViewModel"/>; the code-behind only
/// sets the DataContext (keeps the XAML binding targets honest) and owns the few
/// UI-only concerns the VM stays clear of: opening the Python setup popup when the VM
/// asks (L7), raising the folder/file pickers for the Settings path overrides (L9),
/// and the mod-sources trust modal that gates first installs from a third-party repo (2a R5).
/// The nav rail switches pages through the VM's <c>SelectSectionCommand</c>, so there is
/// nothing here about navigation either.
/// </summary>
public partial class MainWindow : Window
{
    private bool _setupOpen;
    private bool _whatsNewOpen;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        vm.RequestPythonSetup += (_, _) => ShowPythonSetup(vm);
        vm.RequestBrowseContentRoot += (_, _) => BrowseContentRoot(vm);
        vm.RequestBrowseGameExe += (_, _) => BrowseGameExe(vm);
        vm.RequestTrustApproval = ShowTrustAsync;
        vm.RequestWhatsNew = ShowWhatsNew;

        // 2b: restore the last window size (AppConfig already clamped it to the
        // minimum / larger default). The XAML values are just the design-time hint.
        Width = vm.WindowWidth;
        Height = vm.WindowHeight;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Persist the size only for a normal (non-maximised) window; a maximised
        // RestoreBounds is what the user would get next launch, and writing the
        // maximised outer size would drift the window larger every session.
        if (WindowState == WindowState.Normal && DataContext is MainViewModel vm)
            vm.SaveWindowSize(Width, Height);
        base.OnClosing(e);
    }


    private Task<bool> ShowTrustAsync(ModSource source, RemoteModEntry entry)
    {
        // The VM hands us the source + already-parsed entry so we do not need to
        // re-resolve the SHA here; the entry's folderPathInRepo + Files carry what
        // the user needs to see before typing INSTALL.
        var vm = (MainViewModel)DataContext;
        // We have the pinned sha from the last refresh; if the state was cleared we
        // show "(not fetched)" rather than lie about a SHA we do not know.
        var sha = vm.Sources.FirstOrDefault(s => s.Key == source.Key)?.Source.LastSha ?? "";
        var win = new TrustInstallWindow(source, sha, entry) { Owner = this };
        return Task.FromResult(win.ShowDialog() == true);
    }

    // 2b: the What's-new seam. Runs on the dispatcher (the VM raises it from a
    // startup continuation with no ConfigureAwait(false)), so ShowDialog is safe.
    // Guarded so a duplicate raise never stacks a second window.
    private void ShowWhatsNew(string title, string body)
    {
        if (_whatsNewOpen) return;
        _whatsNewOpen = true;
        try
        {
            new WhatsNewWindow(title, body) { Owner = this }.ShowDialog();
        }
        finally
        {
            _whatsNewOpen = false;
        }
    }

    private void ShowPythonSetup(MainViewModel vm)
    {
        if (_setupOpen) return;   // don't stack the guide if a re-check re-raises it
        _setupOpen = true;
        try
        {
            var win = new PythonSetupWindow(
                vm.PythonStatusText, vm.PythonDownloadUrl, vm.PythonPipCommand, vm.RecheckPython)
            {
                Owner = this,
            };
            win.ShowDialog();
        }
        finally
        {
            _setupOpen = false;
        }
    }

    // L9: the picker sets the VM override property, whose setter persists and acts
    // (re-scan for a content root, refresh the launch target for a game exe).
    private void BrowseContentRoot(MainViewModel vm)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the launcher content root" };
        if (dlg.ShowDialog(this) == true) vm.ContentRootOverride = dlg.FolderName;
    }

    private void BrowseGameExe(MainViewModel vm)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose the game executable",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true) vm.GameExeOverride = dlg.FileName;
    }
}
