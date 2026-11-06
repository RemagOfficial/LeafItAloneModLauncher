using System;
using System.Threading;
using System.Threading.Tasks;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// 2b: the self-update check. Compares the installed <see cref="AppInfo.Version"/>
/// against the repo's latest GitHub release tag. Deliberately best-effort: a network
/// failure, a rate-limit, or a repo with no releases all resolve to "unknown" so a
/// launch is never blocked or nagged. The core comparison is a pure function so the
/// headless suite can exercise it with no network at all.
/// </summary>
public sealed class UpdateChecker
{
    private readonly GitHubApi _api;

    public UpdateChecker(GitHubApi? api = null) => _api = api ?? new GitHubApi();

    /// <summary>Outcome of a check. Unknown when GitHub could not be reached.</summary>
    public sealed record Result(bool Known, bool UpdateAvailable, string LatestVersion, string Url, string Body);

    public static readonly Result Unknown = new(false, false, "", "", "");

    /// <summary>
    /// Check the latest release. Never throws: any <see cref="GitHubApiException"/> or
    /// other transport error yields <see cref="Unknown"/>.
    /// </summary>
    public async Task<Result> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            var rel = await _api.GetLatestReleaseAsync(AppInfo.Owner, AppInfo.Repo, ct).ConfigureAwait(false);
            if (rel is null) return Unknown;                       // no releases published yet
            var latest = Normalize(rel.Tag);
            if (!IsNewer(latest, AppInfo.Version, out var parsedLatest)) return new Result(true, false, latest, rel.Url, rel.Body);
            return new Result(true, true, parsedLatest.ToString(3), rel.Url, rel.Body);
        }
        catch (Exception)
        {
            return Unknown;
        }
    }

    /// <summary>
    /// True when <paramref name="remoteTag"/> is a higher version than <paramref name="localVersion"/>.
    /// Tolerates a leading <c>v</c>/<c>vb</c> and an optional <c>.build</c>/<c>+meta</c>. A
    /// remote that does not parse as a version is never "newer" (no false positives). This is
    /// the pure, testable core the async path delegates to.
    /// </summary>
    public static bool IsNewer(string remoteTag, string localVersion, out Version parsed)
    {
        parsed = new Version(0, 0);
        var remote = Normalize(remoteTag);
        var local = Normalize(localVersion);
        if (!Version.TryParse(remote, out var r) || !Version.TryParse(local, out var l))
            return false;
        // Coerce to 2-part so "1.2" vs "1.2.0" compare equal, and to catch the common
        // "1.1" vs "1.1.0" edge. Version normalises missing fields to -1 otherwise.
        l = new Version(Math.Max(l.Major, 0), Math.Max(l.Minor, 0), Math.Max(l.Build, 0));
        r = new Version(Math.Max(r.Major, 0), Math.Max(r.Minor, 0), Math.Max(r.Build, 0));
        if (r > l) { parsed = r; return true; }
        return false;
    }

    /// <summary>Trim whitespace, a leading v/V, and a trailing +metadata suffix.</summary>
    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s.Substring(1);
        var plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        var dash = s.IndexOf('-');            // drop a -prerelease tail for the numeric compare
        if (dash >= 0) s = s[..dash];
        return s.Trim();
    }
}
