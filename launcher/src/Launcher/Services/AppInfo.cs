using System;
using System.Reflection;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// 2b: the launcher's own identity — the single place the app version and its
/// GitHub repo slug are read from. The version comes from the assembly (set by
/// <c>&lt;Version&gt;</c> in the csproj), so the self-update check, the "What's
/// new" gate and the Settings/about line all agree on one number instead of
/// scattering literals. Nothing here touches the network.
/// </summary>
public static class AppInfo
{
    /// <summary>Owner/repo the default mod source and the releases live under.</summary>
    public const string Owner = "RemagOfficial";
    public const string Repo = "LeafItAloneModLauncher";
    public const string OwnerRepo = Owner + "/" + Repo;

    /// <summary>Release tags are the version with a leading <c>v</c> (e.g. <c>v1.0.0</c>).</summary>
    public const string TagPrefix = "v";

    /// <summary>
    /// The current version as <c>major.minor.patch</c>. Reads the informational
    /// version, stripped of any <c>+metadata</c> / <c>-prerelease</c> tail so
    /// <see cref="Version.TryParse"/> succeeds on a dev build; falls back to
    /// <c>1.0.0</c> if the assembly carries no version (e.g. a hand-run DLL).
    /// </summary>
    public static string Version
    {
        get
        {
            var raw =
                typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
                ?? Fallback;

            // Strip a '+' metadata suffix (source SHA etc. SourceLink appends one).
            var plus = raw.IndexOf('+');
            if (plus >= 0) raw = raw[..plus];
            // Strip a leading 'v' if someone set the informational version that way.
            if (raw.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) raw = raw[1..];
            return string.IsNullOrWhiteSpace(raw) ? Fallback : raw.Trim();
        }
    }

    /// <summary>What we report when the assembly has no usable version.</summary>
    public const string Fallback = "1.0.0";

    /// <summary>The release tag for a given bare version string.</summary>
    public static string TagFor(string version) => TagPrefix + version;
}
