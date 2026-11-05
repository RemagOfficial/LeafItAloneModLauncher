using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;

namespace LeafItAlone.Launcher.Views;

/// <summary>
/// The L7 guidance popup shown when no usable Python + UnityPy runtime is detected.
/// It is intentionally a leaf: it only points the user at the pinned interpreter and the
/// one-time pip install, and offers a Re-check that re-runs the launcher's probe without a
/// restart. All data comes in through the constructor so the window stays independent of the VM.
/// </summary>
public partial class PythonSetupWindow : Window
{
    private readonly Action? _recheck;

    public PythonSetupWindow(string statusText, string downloadUrl, string pipCommand, Action? recheck)
    {
        InitializeComponent();
        StatusLine.Text = statusText;
        DownloadLink.NavigateUri = new Uri(downloadUrl, UriKind.Absolute);
        PipBox.Text = pipCommand;
        _recheck = recheck;
    }

    private void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // No browser association is non-fatal; the URL is also shown as the link text.
        }
        e.Handled = true;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(PipBox.Text); }
        catch { /* clipboard can be locked by another process; ignore */ }
    }

    private void OnRecheck(object sender, RoutedEventArgs e)
    {
        _recheck?.Invoke();   // clears the once-guard and re-probes; this window closes now
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
