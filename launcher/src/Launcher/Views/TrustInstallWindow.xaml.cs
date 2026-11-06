using System.Collections.Generic;
using System.Linq;
using System.Windows;
using LeafItAlone.Launcher.Services;

namespace LeafItAlone.Launcher.Views;

/// <summary>
/// Mod-sources (2a R5): modal shown before the first install from a non-trusted
/// source. Displays the repo URL, pinned commit SHA and the list of script files
/// that will land on the user's disk (apply.ps1, revert.ps1, any *.py) so the
/// user cannot say they were shown only a name. The Install button stays disabled
/// until they type the word <c>INSTALL</c> exactly.
/// </summary>
public sealed partial class TrustInstallWindow : Window
{
    public sealed class ViewModel
    {
        public string SourceUrl { get; init; } = "";
        public string SourceSha { get; init; } = "";
        public string ModLine { get; init; } = "";
        public IReadOnlyList<string> ScriptLines { get; init; } = new List<string>();

        private string _typed = "";
        public string TypedWord
        {
            get => _typed;
            set { _typed = value; OnPropertyChanged(nameof(TypedWord)); OnPropertyChanged(nameof(CanInstall)); }
        }
        public bool CanInstall => _typed == "INSTALL";

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged(string n) =>
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(n));
    }

    public TrustInstallWindow(ModSource source, string sha, RemoteModEntry entry)
    {
        var scripts = entry.Files
            .Where(f => !f.IsDirectory)
            .Where(f =>
            {
                var name = System.IO.Path.GetFileName(f.Path).ToLowerInvariant();
                return name.EndsWith(".ps1") || name.EndsWith(".py") || name.EndsWith(".cmd") || name.EndsWith(".bat") || name.EndsWith(".exe");
            })
            .Select(f => f.Path)
            .ToList();
        if (scripts.Count == 0) scripts.Add("(no executable scripts detected)");

        DataContext = new ViewModel
        {
            SourceUrl = source.Url,
            SourceSha = string.IsNullOrEmpty(sha) ? "(unresolved)" : sha,
            ModLine = $"{entry.Name} v{entry.Version} ({entry.Id})",
            ScriptLines = scripts,
        };
        InitializeComponent();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { DialogResult = true; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; Close(); }
}
