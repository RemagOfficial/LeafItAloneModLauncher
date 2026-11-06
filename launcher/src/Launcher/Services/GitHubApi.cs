using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LeafItAlone.Launcher.Services;

/// <summary>One directory entry from GitHub's contents API, normalised.</summary>
public sealed record GitHubEntry(
    string Name,
    string Path,
    string Type,          // "file" | "dir" | "symlink" | "submodule"
    string Sha,           // per-object blob sha; GitHub returns this for both files and dirs
    long Size,
    string? DownloadUrl);

/// <summary>One GitHub release, normalised to the fields the UI needs (2b self-update / What's new).</summary>
public sealed record ReleaseInfo(string Tag, string Url, string Body);

/// <summary>Rate-limit snapshot read from response headers. All zeros when GitHub omits them.</summary>
public sealed record RateLimitState(int Remaining, DateTimeOffset ResetUtc)
{
    public static readonly RateLimitState Unknown = new(0, DateTimeOffset.MinValue);
    public bool IsExhausted => Remaining == 0 && ResetUtc > DateTimeOffset.UtcNow;
    public TimeSpan TimeUntilReset =>
        ResetUtc <= DateTimeOffset.UtcNow ? TimeSpan.Zero : ResetUtc - DateTimeOffset.UtcNow;
}

/// <summary>
/// Thin GitHub REST API client (mod-sources 2a R2). Uses the framework
/// <see cref="HttpClient"/> only, so the launcher still builds fully offline
/// against the base class library. Unauthenticated by design - the request
/// budget (~60/hr) is more than enough for a handful of mods and refreshes
/// because we cache by commit SHA (see <see cref="GitHubSourceService"/>).
/// Every call surfaces rate-limit headers via <see cref="LastRateLimit"/> so
/// the UI can show "GitHub rate limit hit, retry in Xm" instead of a bare 403.
/// </summary>
public sealed class GitHubApi : IDisposable
{
    /// <summary>GitHub requires a User-Agent; this one names the app so ops can trace abuse.</summary>
    public const string UserAgent = "LeafItAloneLauncher/1.0 (+https://github.com/RemagOfficial/LeafItAloneModLauncher)";

    private static readonly HttpClient Http = CreateClient();

    private readonly TimeSpan _timeout;

    public GitHubApi(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Most recent rate-limit snapshot; the UI reads this after any operation.</summary>
    public RateLimitState LastRateLimit { get; private set; } = RateLimitState.Unknown;

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.Clear();
        c.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        // GitHub's contents API returns a JSON body regardless of Accept, but
        // setting application/json is the documented norm and prevents odd
        // redirects if a caller accidentally hits the html endpoint.
        c.DefaultRequestHeaders.Accept.Clear();
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return c;
    }

    /// <summary>
    /// Resolve <paramref name="refName"/> (branch, tag, or full SHA) to a commit SHA.
    /// An empty <paramref name="refName"/> uses the repo's default branch (GitHub
    /// interprets a missing ref on this endpoint that way).
    /// </summary>
    public async Task<string> ResolveShaAsync(string owner, string repo, string refName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("owner required", nameof(owner));
        if (string.IsNullOrWhiteSpace(repo)) throw new ArgumentException("repo required", nameof(repo));

        // A ref may be omitted -> ask for the default branch via the repo endpoint.
        if (string.IsNullOrWhiteSpace(refName))
        {
            using var resp = await GetAsync($"https://api.github.com/repos/{owner}/{repo}", ct).ConfigureAwait(false);
            using var doc = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
            var sha = ReadString(doc.RootElement, "default_branch");
            if (string.IsNullOrEmpty(sha)) throw new GitHubApiException("repo has no default branch");
            refName = sha!;
        }

        // /git/ref/{owner}/{repo}/heads|tags/{name} is more precise but needs to know if
        // the ref is a branch or tag; the commits endpoint accepts either, so use it.
        var escaped = Uri.EscapeDataString(refName);
        using var commitResp = await GetAsync($"https://api.github.com/repos/{owner}/{repo}/commits/{escaped}", ct).ConfigureAwait(false);
        using var commitDoc = await ReadJsonAsync(commitResp, ct).ConfigureAwait(false);
        var sha2 = ReadString(commitDoc.RootElement, "sha");
        if (string.IsNullOrEmpty(sha2)) throw new GitHubApiException("commit response missing sha");
        return sha2!;
    }

    /// <summary>
    /// List one directory. GitHub's contents API returns an array for a directory
    /// and an object for a single file; the file case is treated as a one-entry list
    /// so callers can use the same code path.
    /// </summary>
    public async Task<IReadOnlyList<GitHubEntry>> ListDirAsync(
        string owner, string repo, string shaOrRef, string path, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/contents/{NormalizePath(path)}?ref={Uri.EscapeDataString(shaOrRef)}";
        using var resp = await GetAsync(url, ct).ConfigureAwait(false);
        using var doc = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        var list = new List<GitHubEntry>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in root.EnumerateArray()) list.Add(ParseEntry(el));
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            list.Add(ParseEntry(root));
        }
        return list;
    }

    /// <summary>
    /// Fetch a file's raw bytes. Uses the "raw" media type so GitHub returns the file
    /// content directly instead of the JSON-wrapped base64 body, saving a decode step.
    /// Returns the bytes plus the blob SHA GitHub recorded for content (used by the
    /// per-file manifest in <see cref="LauncherState"/>'s remote block).
    /// </summary>
    public async Task<(byte[] Bytes, string Sha)> DownloadFileAsync(
        string owner, string repo, string shaOrRef, string path, CancellationToken ct = default)
    {
        var url = $"https://raw.githubusercontent.com/{owner}/{repo}/{shaOrRef}/{NormalizePath(path)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // Prefer raw; fall back handled implicitly because we asked for the raw URL, not api/.
        req.Headers.Accept.Clear();
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.raw"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        CaptureRateLimit(resp.Headers);
        if (!resp.IsSuccessStatusCode)
            throw new GitHubApiException($"raw download failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", resp.StatusCode);
        using var ms = new MemoryStream();
        await resp.Content.CopyToAsync(ms, cts.Token).ConfigureAwait(false);
        var bytes = ms.ToArray();
        // The raw endpoint does NOT include the blob sha, so compute it (git blob header
        // + content, then SHA1) to match what GitHub reports in the contents listing.
        var sha = ComputeGitBlobSha(bytes);
        return (bytes, sha);
    }

    /// <summary>
    /// 2b: the repo's most recent published release (<c>releases/latest</c>), used by
    /// the self-update check. GitHub returns 404 for a repo with no releases; that is
    /// surfaced as a <c>null</c> rather than an exception so startup stays silent.
    /// </summary>
    public async Task<ReleaseInfo?> GetLatestReleaseAsync(string owner, string repo, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
        return await FetchReleaseAsync(url, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 2b: the release carrying a specific tag (<c>releases/tags/{tag}</c>), used to
    /// show "What's new" for the version actually installed. A missing tag returns
    /// <c>null</c> (GitHub 404), never a throw.
    /// </summary>
    public async Task<ReleaseInfo?> GetReleaseByTagAsync(string owner, string repo, string tag, CancellationToken ct = default)
    {
        var url = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";
        return await FetchReleaseAsync(url, ct).ConfigureAwait(false);
    }

    private async Task<ReleaseInfo?> FetchReleaseAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        HttpResponseMessage resp;
        try
        {
            resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitHubApiException("GitHub request timed out");
        }
        using (resp)
        {
            CaptureRateLimit(resp.Headers);
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;   // no such release yet
            if (!resp.IsSuccessStatusCode)
                throw new GitHubApiException($"GitHub release lookup failed: {(int)resp.StatusCode} {resp.ReasonPhrase}", resp.StatusCode);
            using var doc = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
            var root = doc.RootElement;
            var tag = ReadString(root, "tag_name") ?? "";
            var htmlUrl = ReadString(root, "html_url") ?? "";
            var body = ReadString(root, "body") ?? "";
            return new ReleaseInfo(tag, htmlUrl, body);
        }
    }

    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        HttpResponseMessage resp;
        try
        {
            resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GitHubApiException("GitHub request timed out");
        }
        CaptureRateLimit(resp.Headers);
        if (!resp.IsSuccessStatusCode)
        {
            var msg = $"GitHub request failed: {(int)resp.StatusCode} {resp.ReasonPhrase} ({url})";
            resp.Dispose();
            throw new GitHubApiException(msg, resp.StatusCode == 0 ? null : (HttpStatusCode?)LastStatusFromMessage(msg));
        }
        return resp;
    }

    private static HttpStatusCode LastStatusFromMessage(string msg)
    {
        // Best-effort: parse "403" out of the message we just formatted above.
        var idx = msg.IndexOf("failed: ", StringComparison.Ordinal);
        if (idx < 0) return HttpStatusCode.InternalServerError;
        var start = idx + 8;
        var end = start;
        while (end < msg.Length && char.IsDigit(msg[end])) end++;
        if (end == start) return HttpStatusCode.InternalServerError;
        return (HttpStatusCode)int.Parse(msg.Substring(start, end - start));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    private static string? ReadString(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static GitHubEntry ParseEntry(JsonElement el)
    {
        var name = ReadString(el, "name") ?? "";
        var path = ReadString(el, "path") ?? "";
        var type = ReadString(el, "type") ?? "file";
        var sha = ReadString(el, "sha") ?? "";
        long size = el.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var s) ? s : 0;
        var dl = ReadString(el, "download_url");
        return new GitHubEntry(name, path, type, sha, size, dl);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        return path.TrimStart('/').Replace('\\', '/');
    }

    private void CaptureRateLimit(HttpResponseHeaders headers)
    {
        int remaining = -1;
        long resetUnix = 0;
        if (headers.TryGetValues("x-ratelimit-remaining", out var remVals))
        {
            foreach (var v in remVals) if (int.TryParse(v, out var r)) { remaining = r; break; }
        }
        if (headers.TryGetValues("x-ratelimit-reset", out var rstVals))
        {
            foreach (var v in rstVals) if (long.TryParse(v, out var t)) { resetUnix = t; break; }
        }
        LastRateLimit = new RateLimitState(
            remaining < 0 ? 0 : remaining,
            resetUnix == 0 ? DateTimeOffset.MinValue : DateTimeOffset.FromUnixTimeSeconds(resetUnix));
    }

    /// <summary>
    /// Compute the git blob SHA1 (used by GitHub's contents API to identify files).
    /// Formula: SHA1( "<c2c3c4c5c6c7c8c9ca>\\0" + content ). We hex-encode lowercase to
    /// match GitHub's response so a stored manifest can be compared directly.
    /// </summary>
    public static string ComputeGitBlobSha(byte[] content)
    {
        // 40 hex chars, 20 bytes; SHA1 is available on all supported TFMs.
        var header = System.Text.Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        using var sha1 = SHA1.Create();
        // Efficient path: TransformBlock on each chunk so we don't allocate a merged buffer.
        sha1.TransformBlock(header, 0, header.Length, null, 0);
        if (content.Length > 0)
            sha1.TransformBlock(content, 0, content.Length, null, 0);
        sha1.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha1.Hash!).ToLowerInvariant();
    }

    public void Dispose()
    {
        // The static HttpClient is intentionally shared across instances (WPF apps
        // create many short-lived clients). Nothing to dispose here; kept for API
        // symmetry so `using var api = new GitHubApi()` is valid.
    }
}

/// <summary>Structured failure so the UI can show a friendly one-liner instead of a stack.</summary>
public sealed class GitHubApiException : Exception
{
    public HttpStatusCode? Status { get; }
    public GitHubApiException(string message) : base(message) { }
    public GitHubApiException(string message, HttpStatusCode? status) : base(message) { Status = status; }
}
