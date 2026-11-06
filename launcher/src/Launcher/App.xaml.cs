using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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
        // Best-effort crash journal so a UI-thread exception on tab switch is
        // diagnosable without attaching a debugger. Written next to settings.json
        // so it lives with the launcher's other state.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var cfg = AppConfig.Load(AppConfig.DefaultPath());
        ThemeManager.Apply(cfg.Theme, cfg.Accent);
        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        TryWriteCrash("dispatcher", args.Exception);
        // Leave Handled=false so the user still sees WPF's default crash dialog;
        // the log line is what we can inspect afterwards.
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs args)
    {
        TryWriteCrash("appdomain", args.ExceptionObject as Exception ?? new Exception("non-exception payload"));
    }

    private static void TryWriteCrash(string channel, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(AppConfig.DefaultPath()) ?? ".";
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {channel}: {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}\r\n");
        }
        catch { /* last resort; don't mask the original crash */ }
    }
}
