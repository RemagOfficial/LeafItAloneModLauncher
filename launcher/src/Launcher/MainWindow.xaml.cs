using System.Windows;
using LeafItAlone.Launcher.ViewModels;
using LeafItAlone.Launcher.Views;

namespace LeafItAlone.Launcher;

/// <summary>
/// Shell window. All state lives in <see cref="MainViewModel"/>; the code-behind only
/// sets the DataContext (keeps the XAML binding targets honest) and owns the few
/// UI-only concerns the VM stays clear of: opening the Python setup popup when the VM
/// asks (L7), and raising the folder/file pickers for the Settings path overrides (L9).
/// The nav rail switches pages through the VM's <c>SelectSectionCommand</c>, so there is
/// nothing here about navigation either.
/// </summary>
public partial class MainWindow : Window
{
    private bool _setupOpen;

    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainViewModel();
        DataContext = vm;
        vm.RequestPythonSetup += (_, _) => ShowPythonSetup(vm);
        vm.RequestBrowseContentRoot += (_, _) => BrowseContentRoot(vm);
        vm.RequestBrowseGameExe += (_, _) => BrowseGameExe(vm);
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
