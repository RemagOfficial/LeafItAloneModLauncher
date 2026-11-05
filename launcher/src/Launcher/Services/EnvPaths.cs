using System;
using System.Text.RegularExpressions;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// Expands <c>%VAR%</c> tokens in manifest paths (e.g. the config store
/// <c>%LOCALAPPDATA%\LeafItAloneSafe\bag-capacity.json</c>) so a shipped manifest
/// stays drive-agnostic. The game lives on <c>B:</c> and the profile on <c>C:</c>,
/// so every path the launcher uses is resolved to an absolute one at runtime —
/// never derived relative to the executable.
/// </summary>
public static class EnvPaths
{
    // %NAME% where NAME is a typical env-var token (letters, digits, underscore).
    private static readonly Regex Token = new(@"%([A-Za-z_][A-Za-z0-9_]*)%", RegexOptions.Compiled);

    /// <summary>
    /// Replace every <c>%NAME%</c> with its environment value. An unknown variable
    /// is left untouched (so a bad token surfaces as a readable path rather than a
    /// silently-empty one the caller can't diagnose).
    /// </summary>
    public static string Expand(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        return Token.Replace(path, m =>
        {
            var val = Environment.GetEnvironmentVariable(m.Groups[1].Value);
            return string.IsNullOrEmpty(val) ? m.Value : val;
        });
    }
}
