using System.Windows;
using LeafItAlone.Launcher.Services;

namespace LeafItAlone.Launcher;

/// <summary>
/// Application entry point. WPF generates <c>InitializeComponent</c> from App.xaml.
/// On startup we load the saved <see cref="AppConfig"/> and apply its theme + accent
/// BEFORE the <c>StartupUri</c> window is created, so the app never flashes the wrong
/// palette (L9). The window is instantiated by <see cref="OnStartup"/> only after
/// <see cref="Application.OnStartup(StartupEventArgs)"/> runs, so applying first is safe.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var cfg = AppConfig.Load(AppConfig.DefaultPath());
        ThemeManager.Apply(cfg.Theme, cfg.Accent);
        base.OnStartup(e);
    }
}
