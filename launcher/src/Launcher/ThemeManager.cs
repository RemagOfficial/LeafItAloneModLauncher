using System;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// Owns the live light/dark + accent swap (L9) entirely in code, with no third-party
/// theme package (the offline constraint). Every style in <c>Theme/Controls.xaml</c>
/// references its brushes by <c>DynamicResource</c>, so replacing the merged palette
/// dictionaries at runtime re-skins the whole UI immediately - no restart, no window
/// rebuild.
///
/// <para>Dictionary order in <see cref="Application.Resources"/> is:
/// Controls (styles) &rarr; Light/Dark (theme palette) &rarr; accent (a code-built
/// override). The last wins, so a picked accent shades over whichever theme is active.
/// The pure helpers (<see cref="Lighten"/>/<see cref="Darken"/>/<see cref="OnAccent"/>/
/// <see cref="ParseHex"/>/<see cref="ResolveEffective"/>) touch no WPF state and are
/// what the headless checks assert.</para>
/// </summary>
public static class ThemeManager
{
    private const string LightSource = "/Theme/Light.xaml";
    private const string DarkSource = "/Theme/Dark.xaml";

    // Held so re-applying an accent replaces the same slot instead of stacking dicts.
    private static ResourceDictionary? _accentDict;

    public static string CurrentTheme { get; private set; } = AppConfig.ThemeLight;

    /// <summary>Apply a theme choice + accent now. <paramref name="theme"/> may be <c>system</c>.</summary>
    public static void Apply(string theme, string accent)
    {
        var resources = Application.Current?.Resources;
        if (resources is null) return;

        var effective = ResolveEffective(theme);
        SwapThemeDictionary(resources, effective);
        ApplyAccent(resources, accent);
        CurrentTheme = effective;
    }

    private static void SwapThemeDictionary(ResourceDictionary resources, string effective)
    {
        var source = effective == AppConfig.ThemeDark ? DarkSource : LightSource;

        // Drop whichever palette is currently merged so the swap is idempotent.
        for (var i = resources.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            var s = resources.MergedDictionaries[i].Source?.OriginalString;
            if (s is not null && (s.EndsWith(LightSource) || s.EndsWith(DarkSource)))
                resources.MergedDictionaries.RemoveAt(i);
        }

        var dict = new ResourceDictionary { Source = new Uri(Pack(source), UriKind.Absolute) };
        // Insert before the accent override (kept last) if it is already present.
        var index = _accentDict is not null
            ? resources.MergedDictionaries.IndexOf(_accentDict)
            : -1;
        if (index >= 0) resources.MergedDictionaries.Insert(index, dict);
        else resources.MergedDictionaries.Add(dict);
    }

    private static void ApplyAccent(ResourceDictionary resources, string accent)
    {
        var baseColor = ParseHex(accent);
        var hover = Lighten(baseColor, 0.10);
        var pressed = Darken(baseColor, 0.14);
        var onAccent = OnAccent(baseColor);

        var dict = _accentDict ?? new ResourceDictionary();
        dict["AccentBrush"] = Freeze(baseColor);
        dict["AccentHoverBrush"] = Freeze(hover);
        dict["AccentPressedBrush"] = Freeze(pressed);
        dict["OnAccentBrush"] = Freeze(onAccent);

        if (_accentDict is null)
        {
            _accentDict = dict;
            resources.MergedDictionaries.Add(dict);   // last wins over the theme palette
        }
    }

    private static string Pack(string relative) =>
        "pack://application:,,,/LeafItAloneLauncher;component" + relative;

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---- pure, unit-testable helpers ----

    /// <summary>Resolve <c>system</c> to the concrete light/dark in effect (defaults light on any error).</summary>
    public static string ResolveEffective(string theme) => theme switch
    {
        AppConfig.ThemeLight => AppConfig.ThemeLight,
        AppConfig.ThemeDark => AppConfig.ThemeDark,
        _ => IsSystemDark() ? AppConfig.ThemeDark : AppConfig.ThemeLight,
    };

    /// <summary>Read the Windows <c>AppsUseLightTheme</c> hint; anything unusual is treated as light.</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var v = key?.GetValue("AppsUseLightTheme");
            return v is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Parse <c>#RRGGBB</c>; an invalid string falls back to the house green.</summary>
    public static Color ParseHex(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try { return (Color)ColorConverter.ConvertFromString(hex.Trim())!; }
            catch { /* fall through to default */ }
        }
        return (Color)ColorConverter.ConvertFromString(AppConfig.DefaultAccent)!;
    }

    /// <summary>Blend toward white by <paramref name="t"/> (0..1).</summary>
    public static Color Lighten(Color c, double t) => Blend(c, Colors.White, t);

    /// <summary>Blend toward black by <paramref name="t"/> (0..1).</summary>
    public static Color Darken(Color c, double t) => Blend(c, Colors.Black, t);

    private static Color Blend(Color from, Color to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb(
            (byte)Channeled(from.R, to.R, t),
            (byte)Channeled(from.G, to.G, t),
            (byte)Channeled(from.B, to.B, t));
    }

    private static double Channeled(byte from, byte to, double t) => from + (to - from) * t;

    /// <summary>Readable foreground for a filled accent button: white on dark accents, near-black on light ones.</summary>
    public static Color OnAccent(Color accent)
    {
        // Perceived luminance (Rec. 601); mid/high-luma accents get dark text.
        var luma = 0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B;
        return luma > 158 ? Color.FromRgb(0x14, 0x17, 0x14) : Colors.White;
    }
}
