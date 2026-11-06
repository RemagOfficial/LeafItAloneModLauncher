using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// One entry in <c>sources.json</c>: a mod-source repo the launcher can browse.
/// The <see cref="Key"/> is a stable, filesystem-safe identifier derived from
/// <see cref="Url"/> so a source's cache and installed-mod tree survive a URL
/// rename (fork moves, ssh-to-https swap) as long as owner+repo stay the same.
/// </summary>
public sealed class ModSource
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";

    /// <summary>GitHub repository URL (https://github.com/owner/repo or owner/repo shorthand).</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    /// <summary>Branch, tag or commit the source is pinned to. Empty means the default branch.</summary>
    [JsonPropertyName("ref")] public string RefName { get; set; } = "";

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>
    /// False for user-added third-party sources: installing from them shows the
    /// "type INSTALL to run scripts from this repo" modal (plan R5). True for
    /// the built-in launcher repo and for anything the user has explicitly
    /// marked trusted after seeing the warning once.
    /// </summary>
    [JsonPropertyName("trusted")] public bool Trusted { get; set; }

    /// <summary>ISO-8601 UTC stamp of the last successful listing refresh, or empty.</summary>
    [JsonPropertyName("lastFetchedUtc")] public string LastFetchedUtc { get; set; } = "";

    /// <summary>Commit SHA the last successful listing resolved to; drives the "update available" badge.</summary>
    [JsonPropertyName("lastSha")] public string LastSha { get; set; } = "";

    /// <summary>
    /// Sanitize a GitHub URL into a filesystem-safe key (lowercase, alnum + dash).
    /// Accepts <c>owner/repo</c>, <c>https://github.com/owner/repo</c> (with .git,
    /// trailing slash, or extra path segments) and any-case owner/repo.
    /// </summary>
    public static string DeriveKey(string urlOrShorthand)
    {
        var (owner, repo) = SplitOwnerRepo(urlOrShorthand);
        if (owner.Length == 0 || repo.Length == 0) return "";
        return Sanitize(owner) + "__" + Sanitize(repo);
    }

    /// <summary>Parse a URL (or <c>owner/repo</c>) into (owner, repo). Returns false if it isn't two path segments.</summary>
    public static bool TryParseOwnerRepo(string urlOrShorthand, out string owner, out string repo)
    {
        (owner, repo) = SplitOwnerRepo(urlOrShorthand);
        return owner.Length > 0 && repo.Length > 0;
    }

    /// <summary>
    /// Strip the scheme + host from a URL (if present) and take the first two path
    /// segments. A bare <c>owner/repo</c> shorthand skips the strip step.
    /// </summary>
    private static (string Owner, string Repo) SplitOwnerRepo(string urlOrShorthand)
    {
        if (string.IsNullOrWhiteSpace(urlOrShorthand)) return ("", "");
        var s = urlOrShorthand.Trim();

        // Only strip host when there IS a scheme+host prefix. For "owner/repo" shorthand
        // the first '/' is the owner/repo separator, not a host/path boundary.
        var schemeIdx = s.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx >= 0)
        {
            s = s[(schemeIdx + 3)..];
            var slashIdx = s.IndexOf('/');
            if (slashIdx >= 0) s = s[(slashIdx + 1)..];
        }

        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return ("", "");
        var repo = parts[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo[..^4];
        return (parts[0], repo);
    }

    private static string Sanitize(string seg)
    {
        var arr = seg.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        // Collapse consecutive dashes and trim edges so the key stays readable.
        var s = new string(arr);
        while (s.Contains("--")) s = s.Replace("--", "-");
        return s.Trim('-');
    }
}

/// <summary>
/// <c>%LOCALAPPDATA%\LeafItAloneSafe\launcher\sources.json</c>: the list of mod-source
/// repos the launcher will browse and install from. Persisted alongside
/// <see cref="AppConfig"/>. Load is fail-safe (missing/corrupt file yields the
/// default list, which contains only the built-in launcher repo).
/// </summary>
public sealed class SourceRegistry
{
    /// <summary>Owner/repo for the built-in mod source, always present unless the user removes it.</summary>
    public const string DefaultRepoOwner = "RemagOfficial";
    public const string DefaultRepoName = "LeafItAloneModLauncher";
    public const string DefaultRepoUrl = "https://github.com/RemagOfficial/LeafItAloneModLauncher";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [JsonPropertyName("sources")] public List<ModSource> Sources { get; set; } = new();

    /// <summary>
    /// When false, the launcher refuses to add new sources and Browse shows only the
    /// default one (plan R5: safe-by-default). Persisted in <see cref="AppConfig"/>,
    /// kept here as a static helper so callers can consult the same token.
    /// </summary>
    public static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "LeafItAloneSafe", "launcher", "sources.json");
    }

    public static SourceRegistry Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return CreateDefault();
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return CreateDefault();
            var reg = JsonSerializer.Deserialize<SourceRegistry>(text);
            if (reg is null) return CreateDefault();
            reg.Normalize();
            // A file with an empty list still gets the default source re-added so the
            // launcher is never left with nothing to browse. Only an explicit removal
            // of every source followed by a save would persist that; we treat that
            // the same as a missing file.
            if (reg.Sources.Count == 0) reg.Sources.Add(MakeDefaultSource());
            return reg;
        }
        catch
        {
            return CreateDefault();
        }
    }

    public void Save(string path)
    {
        Normalize();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, WriteOptions));
    }

    public static SourceRegistry CreateDefault() => new() { Sources = { MakeDefaultSource() } };

    public static ModSource MakeDefaultSource() => new()
    {
        Key = ModSource.DeriveKey(DefaultRepoUrl),
        Url = DefaultRepoUrl,
        RefName = "",   // default branch
        Enabled = true,
        Trusted = true, // our repo; no INSTALL prompt
    };

    /// <summary>
    /// Fill in any missing key from the URL and drop empty entries so a hand-edited
    /// file (or a save from a partially-populated UI) can never leave a source without
    /// a filesystem identifier.
    /// </summary>
    public void Normalize()
    {
        Sources ??= new();
        for (var i = Sources.Count - 1; i >= 0; i--)
        {
            var s = Sources[i];
            if (s is null || string.IsNullOrWhiteSpace(s.Url)) { Sources.RemoveAt(i); continue; }
            if (string.IsNullOrWhiteSpace(s.Key)) s.Key = ModSource.DeriveKey(s.Url);
            if (string.IsNullOrWhiteSpace(s.Key)) { Sources.RemoveAt(i); continue; }   // unparseable
            s.RefName ??= "";
            s.LastSha ??= "";
            s.LastFetchedUtc ??= "";
        }
        // De-duplicate by key: last one wins, so re-adding a source updates it in place.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = Sources.Count - 1; i >= 0; i--)
        {
            if (!seen.Add(Sources[i].Key)) Sources.RemoveAt(i);
        }
    }

    public ModSource? FindByKey(string key) =>
        Sources.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
}
