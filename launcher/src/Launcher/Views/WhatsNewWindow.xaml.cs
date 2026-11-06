using System.Windows;

namespace LeafItAlone.Launcher.Views;

/// <summary>
/// 2b: the first-run-per-version "What's new" dialog. Lightweight by design - it
/// shows the GitHub release notes for the version the user just launched, rendered
/// as selectable plain text (the body is markdown; we do not ship a renderer).
/// Raised once per app version by <see cref="MainViewModel.MaybeShowWhatsNewAsync"/>
/// through the <c>RequestWhatsNew</c> seam, mirroring how <see cref="TrustInstallWindow"/>
/// is owned by the main window and shown with ShowDialog.
/// </summary>
public sealed partial class WhatsNewWindow : Window
{
    public sealed class Info
    {
        public string Title { get; init; } = "What's new";
        public string Body { get; init; } = "";
    }

    public WhatsNewWindow(string title, string markdownBody)
    {
        DataContext = new Info { Title = title, Body = string.IsNullOrWhiteSpace(markdownBody) ? "No release notes were published for this version." : markdownBody };
        InitializeComponent();
    }

    private void Close_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
}
