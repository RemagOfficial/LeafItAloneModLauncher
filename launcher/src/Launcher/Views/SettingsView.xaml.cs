using System.Windows.Controls;

namespace LeafItAlone.Launcher.Views;

/// <summary>
/// The Settings page (L9). Previously a stub that only printed the content root; now it
/// is functional: a live theme + accent picker and the two path overrides (content root,
/// game exe). Every control binds straight to the inherited <c>MainViewModel</c>, which
/// persists to <c>settings.json</c> and applies the change (re-theme, re-scan, or a new
/// launch target) immediately. The folder/file pickers themselves are opened by the
/// window code-behind in response to the VM's browse events.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}
