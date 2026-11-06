using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LeafItAlone.Launcher.Models;

namespace LeafItAlone.Launcher.Services;

/// <summary>One mod listed on a remote source. Cheap (name/description/manifest) but not the file bodies.</summary>
public sealed record RemoteModEntry(
    string Id,
    string Name,
    string Version,
    string Description,
    string Homepage,
    string SourceRepo,
    string GameVersion,
    string FolderPathInRepo,
    IReadOnlyList<RemoteFileEntry> Files,
    string? ManifestError);

/// <summary>A file inside a mod folder on the remote. The <see cref="Sha"/> is GitHub's blob sha.</summary>
public sealed record RemoteFileEntry(string Path, string Sha, long Size, bool IsDirectory);

/// <summary>Snapshot of one source's listing at one commit; drives the Browse tab.</summary>
public sealed record SourceSnapshot(
    string SourceKey,
    string Owner,
    string Repo,
    string Sha,
    DateTimeOffset FetchedUtc,
    IReadOnlyList<RemoteModEntry> Mods,
    IReadOnlyList<string> Errors);

/// <summary>
/// Fetches and parses listings for a <see cref="ModSource"/> over the GitHub REST API
/// (mod-sources 2a R2). Deliberately does no caching of its own; callers decide what
/// to persist and where. Rate-limit state is exposed so the UI can show a friendly
/// "retry in Xm" message when the budget runs out.
/// </summary>
public sealed class GitHubSourceService
{
    private readonly GitHubApi _api;

    public GitHubSourceService(GitHubApi api) => _api = api;

    public RateLimitState RateLimit => _api.LastRateLimit;

    /// <summary>
    /// List every mod a source publishes. Steps:
    ///   1. Resolve the source's <c>refName</c> to a commit SHA (or use the default branch).
    ///   2. Enumerate <c>content-mods/</c>; each subfolder is one mod.
    ///   3. Enumerate each mod's folder recursively (one request per directory), parsing
    ///      <c>mod.json</c> inline (downloaded once) so we get id/name/version/description
    ///      without paying the full file download for the listing view.
    /// Any individual mod that fails to parse is reported in <see cref="SourceSnapshot.Errors"/>
    /// but does not abort the whole listing.
    /// </summary>
    public async Task<SourceSnapshot> FetchListingsAsync(ModSource source, CancellationToken ct = default)
    {
        if (!ModSource.TryParseOwnerRepo(source.Url, out var owner, out var repo))
            throw new GitHubApiException($"source URL is not a GitHub repo: {source.Url}");

        var sha = await _api.ResolveShaAsync(owner, repo, source.RefName, ct).ConfigureAwait(false);

        var snapshot = new List<RemoteModEntry>();
        var errors = new List<string>();

        IReadOnlyList<GitHubEntry> top;
        try
        {
            top = await _api.ListDirAsync(owner, repo, sha, "content-mods", ct).ConfigureAwait(false);
        }
        catch (GitHubApiException ex) when (ex.Status == System.Net.HttpStatusCode.NotFound)
        {
            // A repo without content-mods/ is a valid repo, just not a mod source.
            errors.Add($"repo {owner}/{repo} has no content-mods/ directory at sha {sha[..Math.Min(7, sha.Length)]}");
            return new SourceSnapshot(source.Key, owner, repo, sha, DateTimeOffset.UtcNow, snapshot, errors);
        }

        foreach (var modDir in top.Where(e => string.Equals(e.Type, "dir", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var files = await ListRecursiveAsync(owner, repo, sha, modDir.Path, ct).ConfigureAwait(false);
                var manifestFile = files.FirstOrDefault(f =>
                    !f.IsDirectory && string.Equals(f.Path.Split('/').Last(), "mod.json", StringComparison.OrdinalIgnoreCase));
                string? manifestError = null;
                ModManifest? manifest = null;
                if (manifestFile is null)
                {
                    manifestError = "no mod.json in mod folder";
                }
                else
                {
                    try
                    {
                        var (bytes, _) = await _api.DownloadFileAsync(owner, repo, sha, manifestFile.Path, ct).ConfigureAwait(false);
                        manifest = ParseManifest(bytes);
                    }
                    catch (Exception ex)
                    {
                        manifestError = "mod.json parse failed: " + ex.Message;
                    }
                }

                var id = manifest?.Id;
                if (string.IsNullOrWhiteSpace(id)) id = modDir.Name;   // folder-name fallback, matches local discovery
                snapshot.Add(new RemoteModEntry(
                    Id: id!,
                    Name: manifest?.Name ?? id!,
                    Version: manifest?.Version ?? "",
                    Description: manifest?.Description ?? "",
                    Homepage: manifest?.Homepage ?? "",
                    SourceRepo: manifest?.SourceRepo ?? $"{owner}/{repo}",
                    GameVersion: manifest?.GameVersion ?? "",
                    FolderPathInRepo: modDir.Path,
                    Files: files,
                    ManifestError: manifestError));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add($"{modDir.Name}: {ex.Message}");
            }
        }

        return new SourceSnapshot(source.Key, owner, repo, sha, DateTimeOffset.UtcNow, snapshot, errors);
    }

    /// <summary>
    /// Download one mod's files into <paramref name="destDir"/>, preserving sub-paths
    /// under <c>content-mods/&lt;id&gt;</c>. Files whose SHA already matches on disk are
    /// skipped (this is what makes self-healing cheap). Returns the SHA manifest the
    /// caller persists into LauncherState so future EnsurePresent checks are offline.
    /// </summary>
    public async Task<Dictionary<string, string>> DownloadModAsync(
        ModSource source,
        SourceSnapshot snapshot,
        RemoteModEntry entry,
        string destDir,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Sha)) throw new InvalidOperationException("snapshot has no sha");
        Directory.CreateDirectory(destDir);

        var writtenManifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prefix = entry.FolderPathInRepo.TrimEnd('/') + "/";

        foreach (var f in entry.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (f.IsDirectory)
            {
                var dirPath = Path.Combine(destDir, Relativize(f.Path, prefix));
                Directory.CreateDirectory(dirPath);
                continue;
            }

            var rel = Relativize(f.Path, prefix);
            var localPath = Path.Combine(destDir, rel);
            var parent = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);

            // Skip if a file with a matching blob SHA is already on disk. We compute
            // the git blob SHA locally on read (cheap; a mod file is at most tens of KB).
            if (File.Exists(localPath))
            {
                try
                {
                    var existingBytes = await SafeReadAllBytesAsync(localPath, ct).ConfigureAwait(false);
                    var existingSha = GitHubApi.ComputeGitBlobSha(existingBytes);
                    if (string.Equals(existingSha, f.Sha, StringComparison.OrdinalIgnoreCase))
                    {
                        writtenManifest[rel] = f.Sha;
                        continue;
                    }
                }
                catch { /* fall through and re-download */ }
            }

            var (bytes, _) = await _api.DownloadFileAsync(snapshot.Owner, snapshot.Repo, snapshot.Sha, f.Path, ct).ConfigureAwait(false);
            var tmp = localPath + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            if (File.Exists(localPath)) File.Replace(tmp, localPath, null);
            else File.Move(tmp, localPath);
            writtenManifest[rel] = f.Sha;
        }

        return writtenManifest;
    }

    /// <summary>
    /// Enumerate every file under <paramref name="rootPath"/> breadth-first, one
    /// directory-list request per subdirectory. Depth is bounded (5) because our mods
    /// are flat or one-level deep and a runaway tree is more likely a mis-authored
    /// source than something we should mirror.
    /// </summary>
    private async Task<IReadOnlyList<RemoteFileEntry>> ListRecursiveAsync(
        string owner, string repo, string sha, string rootPath, CancellationToken ct, int maxDepth = 5)
    {
        var all = new List<RemoteFileEntry>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((rootPath, 0));
        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (p, d) = queue.Dequeue();
            var entries = await _api.ListDirAsync(owner, repo, sha, p, ct).ConfigureAwait(false);
            foreach (var e in entries)
            {
                var isDir = string.Equals(e.Type, "dir", StringComparison.OrdinalIgnoreCase);
                all.Add(new RemoteFileEntry(e.Path, e.Sha, e.Size, isDir));
                if (isDir && d + 1 < maxDepth) queue.Enqueue((e.Path, d + 1));
            }
        }
        return all;
    }

    private static string Relativize(string fullPath, string prefix)
    {
        if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return fullPath[prefix.Length..].Replace('/', Path.DirectorySeparatorChar);
        return fullPath.Replace('/', Path.DirectorySeparatorChar);
    }

    private static ModManifest ParseManifest(byte[] json)
    {
        var opts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var m = JsonSerializer.Deserialize<ModManifest>(json, opts)
            ?? throw new InvalidDataException("mod.json is empty");
        if (string.IsNullOrWhiteSpace(m.Apply?.Script))
            throw new InvalidDataException("apply.script is required");
        return m;
    }

    private static async Task<byte[]> SafeReadAllBytesAsync(string path, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        using var ms = new MemoryStream();
        await fs.CopyToAsync(ms, ct).ConfigureAwait(false);
        return ms.ToArray();
    }
}
