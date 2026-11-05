using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// The launcher's own UI preferences (L9), stored beside <c>state.json</c> at
/// <c>%LOCALAPPDATA%\LeafItAloneSafe\launcher\settings.json</c>. Distinct from a mod's
/// config (the values the engines read) and from <see cref="LauncherState"/> (per-mod
/// apply bookkeeping): this only remembers appearance and the two optional path
/// overrides, so the app stays data-driven and a fresh install defaults cleanly.
///
/// <para>Load is fail-safe by design (mirrors <see cref="LauncherState.Load"/>): a
/// missing or corrupt file yields defaults rather than throwing across startup, since
/// none of this is authoritative for the game - it only steers the UI and which
/// existing engine paths are pointed at.</para>
/// </summary>
public sealed class AppConfig
{
    // Theme tokens persisted as lowercase strings so the JSON is hand-editable and
    // matches the segmented control's tags in SettingsView.
    public const string ThemeLight = "light";
    public const string ThemeDark = "dark";
    public const string ThemeSystem = "system";

    /// <summary>Leaf-green house accent (also the App.xaml default) used when unset.</summary>
    public const string DefaultAccent = "#3FA34D";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary><c>light</c> | <c>dark</c> | <c>system</c>. Defaults to following the OS.</summary>
    [JsonPropertyName("theme")] public string Theme { get; set; } = ThemeSystem;

    /// <summary>Accent as <c>#RRGGBB</c>; the hover/pressed shades are derived from it.</summary>
    [JsonPropertyName("accent")] public string Accent { get; set; } = DefaultAccent;

    /// <summary>
    /// Explicit content root to hand <see cref="RepoLocator.TryLocate"/>. Empty means
    /// auto-detect (walk up from the executable). When set it must name a folder that
    /// holds <c>content-mods</c> beside a <c>safe-base</c>/<c>tools</c> marker.
    /// </summary>
    [JsonPropertyName("contentRootOverride")] public string ContentRootOverride { get; set; } = "";

    /// <summary>
    /// Explicit game exe for <see cref="GameLauncher"/>. Empty means read the safe-base
    /// manifest's <c>gameExe</c>. The override wins only when the file exists.
    /// </summary>
    [JsonPropertyName("gameExeOverride")] public string GameExeOverride { get; set; } = "";

    public static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "LeafItAloneSafe", "launcher", "settings.json");
    }

    public static AppConfig Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new AppConfig();
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new AppConfig();
            var cfg = JsonSerializer.Deserialize<AppConfig>(text);
            if (cfg is null) return new AppConfig();
            cfg.Normalize();
            return cfg;
        }
        catch
        {
            return new AppConfig();   // a bad settings file must never brick startup
        }
    }

    public void Save(string path)
    {
        Normalize();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions));
    }

    /// <summary>Clamp stored strings to sane shapes so a hand-edited file can't wed the UI.</summary>
    private void Normalize()
    {
        Theme = Theme?.Trim().ToLowerInvariant() switch
        {
            ThemeLight or ThemeDark or ThemeSystem => Theme.Trim().ToLowerInvariant(),
            _ => ThemeSystem,
        };
        if (string.IsNullOrWhiteSpace(Accent) || !IsValidHex(Accent!)) Accent = DefaultAccent;
        ContentRootOverride ??= "";
        GameExeOverride ??= "";
    }

    private static bool IsValidHex(string s)
    {
        if (s.Length != 7 || s[0] != '#') return false;
        for (var i = 1; i < 7; i++)
        {
            var c = s[i];
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }
        return true;
    }
}
