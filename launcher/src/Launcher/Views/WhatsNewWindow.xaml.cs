using System.Windows;
using LeafItAlone.Launcher.Services;

namespace LeafItAlone.Launcher.Views;

/// <summary>
/// 2b: the first-run-per-version "What's new" dialog. It shows the GitHub release notes
/// for the version the user just launched, rendered from markdown into a themed
/// FlowDocument (see <see cref="MarkdownRenderer"/>) - so headings, lists, links and
/// code read as formatted text instead of raw symbols. Raised once per app version by
/// <see cref="MainViewModel.MaybeShowWhatsNewAsync"/> through the <c>RequestWhatsNew</c>
/// seam, mirroring how <see cref="TrustInstallWindow"/> is owned by the main window and
/// shown with ShowDialog.
/// </summary>
public sealed partial class WhatsNewWindow : Window
{
    public sealed class Info
    {
        public string Title { get; init; } = "What's new";
    }

    public WhatsNewWindow(string title, string markdownBody)
    {
        DataContext = new Info { Title = title };
        InitializeComponent();

        // Render the (possibly empty) release body to themed markdown for the viewer.
        var body = string.IsNullOrWhiteSpace(markdownBody)
            ? "No release notes were published for this version."
            : markdownBody;
        Body.Document = MarkdownRenderer.Render(body);
    }

    private void Close_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
}
