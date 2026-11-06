using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LeafItAlone.Launcher.Models;

namespace LeafItAlone.Launcher.Services;

/// <summary>Outcome of an install / update / self-heal step. Never thrown - the UI reads these.</summary>
public sealed record StagerResult(bool Ok, string Message, int FilesWritten, int FilesSkipped)
{
    public static StagerResult Success(string msg, int w, int s) => new(true, msg, w, s);
    public static StagerResult Fail(string msg) => new(false, msg, 0, 0);
}

/// <summary>
/// Mod-sources (2a R3): copies a remote mod into <c>remote-mods/&lt;sourceKey&gt;/&lt;id&gt;/</c>,
/// records the per-file SHA manifest into <see cref="LauncherState"/>, and heals
/// missing/mismatched files before Apply runs. There is deliberately no Uninstall -
/// mods are tiny and users delete them by hand if they want.
/// </summary>
public sealed class RemoteModStager
{
    private readonly GitHubApi _api;
    private readonly GitHubSourceService _source;

    public RemoteModStager(GitHubApi api, GitHubSourceService source)
    {
        _api = api;
        _source = source;
    }

    /// <summary>
    /// Install (or update) a mod onto disk and stamp the state.
    /// Idempotent: calling it again with the same inputs is a no-op file-wise (all SHAs
    /// match) but still refreshes the recorded <c>installedUtc</c>. Rejects ids that
    /// collide with a built-in mod.
    /// </summary>
    public async Task<StagerResult> InstallAsync(
        ModSource source,
        SourceSnapshot snapshot,
        RemoteModEntry entry,
        LauncherState state,
        IEnumerable<string> builtInModIds,
        string statePath,
        CancellationToken ct = default)
    {
        if (!string.Equals(snapshot.SourceKey, source.Key, StringComparison.OrdinalIgnoreCase))
            return StagerResult.Fail("snapshot source key does not match the requested source");
        if (entry.ManifestError is not null)
            return StagerResult.Fail($"refusing to install: {entry.ManifestError}");

        if (builtInModIds.Any(id => string.Equals(id, entry.Id, StringComparison.OrdinalIgnoreCase)))
            return StagerResult.Fail($"a built-in mod with id '{entry.Id}' already exists; the remote copy was not installed");

        var dest = RepoLocator.RemoteModFolder(source.Key, entry.Id);
        var manifest = await _source.DownloadModAsync(source, snapshot, entry, dest, ct).ConfigureAwait(false);

        var st = state.For(entry.Id);
        st.Remote = new RemoteState
        {
            SourceKey = source.Key,
            SourceUrl = source.Url,
            Sha = snapshot.Sha,
            InstalledUtc = DateTime.UtcNow.ToString("o"),
            FolderPathInRepo = entry.FolderPathInRepo,
            FilesManifest = manifest,
        };
        try { state.Save(statePath); }
        catch (Exception ex) { return StagerResult.Fail($"installed files OK but state save failed: {ex.Message}"); }

        return StagerResult.Success(
            $"installed {entry.Id} at {ShortSha(snapshot.Sha)} ({manifest.Count} file(s))",
            manifest.Count, 0);
    }

    /// <summary>
    /// Verify the mod's files are on disk and match the recorded SHAs, re-downloading
    /// anything missing or stale. Called from <see cref="ApplyOrchestrator"/> before
    /// every Apply of a remote mod. Returns success with 0 files written in the common
    /// "nothing missing" case.
    /// </summary>
    public async Task<StagerResult> EnsurePresentAsync(
        ModInfo mod,
        LauncherState state,
        SourceRegistry sources,
        string statePath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!mod.IsRemote)
            return StagerResult.Success("built-in mod, nothing to heal", 0, 0);

        var st = state.For(mod.Id);
        var remote = st.Remote;
        if (remote is null || string.IsNullOrEmpty(remote.Sha) || remote.FilesManifest.Count == 0)
            return StagerResult.Fail($"remote mod '{mod.Id}' has no recorded provenance; re-install it from the Browse tab");

        var folder = mod.Folder;
        if (!Directory.Exists(folder))
        {
            progress?.Report($"re-downloading {mod.Id} (folder missing: {folder})");
            return await RefetchAsync(remote, mod.Id, folder, state, statePath, ct).ConfigureAwait(false);
        }

        // Cheap scan first: only re-download what actually differs.
        var missing = new List<(string Rel, string ExpectedSha)>();
        foreach (var (rel, expectedSha) in remote.FilesManifest)
        {
            var path = Path.Combine(folder, rel);
            if (!File.Exists(path)) { missing.Add((rel, expectedSha)); continue; }
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
                var actual = GitHubApi.ComputeGitBlobSha(bytes);
                if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                    missing.Add((rel, expectedSha));
            }
            catch { missing.Add((rel, expectedSha)); }
        }

        if (missing.Count == 0)
            return StagerResult.Success($"all {remote.FilesManifest.Count} files verified for {mod.Id}", 0, remote.FilesManifest.Count);

        progress?.Report($"re-downloading {missing.Count} missing/stale file(s) for {mod.Id}");

        // Resolve the source entry (may be missing if the user removed it; we still have the pinned sha+url).
        if (!ModSource.TryParseOwnerRepo(remote.SourceUrl, out var owner, out var repo))
            return StagerResult.Fail($"source URL for '{mod.Id}' is unparseable: {remote.SourceUrl}");

        var written = 0;
        foreach (var (rel, expectedSha) in missing)
        {
            ct.ThrowIfCancellationRequested();
            // Reconstruct the path inside the repo from the mod's folder path prefix.
            var repoPath = RepoJoin(remote, mod.Id, rel);
            try
            {
                var (bytes, _) = await _api.DownloadFileAsync(owner, repo, remote.Sha, repoPath, ct).ConfigureAwait(false);
                // Verify the git blob sha GitHub's raw endpoint produced matches what
                // we recorded. A mismatch means the ref we pinned no longer exists
                // (repo rewritten / GC) OR the path changed under us.
                var actual = GitHubApi.ComputeGitBlobSha(bytes);
                if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    return StagerResult.Fail(
                        $"re-fetch of '{rel}' from {remote.SourceUrl}@{ShortSha(remote.Sha)} returned a different file than recorded " +
                        $"(git history rewritten or repo moved). Reinstall this mod from the Browse tab.");
                }
                var localPath = Path.Combine(folder, rel);
                var parent = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                var tmp = localPath + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                if (File.Exists(localPath)) File.Replace(tmp, localPath, null);
                else File.Move(tmp, localPath);
                written++;
            }
            catch (GitHubApiException ex)
            {
                return StagerResult.Fail($"could not re-download '{rel}': {ex.Message}");
            }
        }

        return StagerResult.Success($"healed {written} file(s) for {mod.Id}", written, remote.FilesManifest.Count - written);
    }

    private async Task<StagerResult> RefetchAsync(
        RemoteState remote, string modId, string folder, LauncherState state, string statePath, CancellationToken ct)
    {
        // Whole-tree re-download path: used when the folder was deleted. We hit the
        // raw endpoint once per recorded file, verifying each blob SHA against the
        // manifest, so no directory listing call is needed (this stays cheap even
        // for the built-in sakura-petals mod which carries a ~40 KB README).
        if (!ModSource.TryParseOwnerRepo(remote.SourceUrl, out var owner, out var repo))
            return StagerResult.Fail($"source URL for '{modId}' is unparseable: {remote.SourceUrl}");

        Directory.CreateDirectory(folder);
        var newManifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var written = 0;
        foreach (var (rel, expectedSha) in remote.FilesManifest)
        {
            ct.ThrowIfCancellationRequested();
            var repoPath = RepoJoin(remote, modId, rel);
            try
            {
                var (bytes, _) = await _api.DownloadFileAsync(owner, repo, remote.Sha, repoPath, ct).ConfigureAwait(false);
                var actual = GitHubApi.ComputeGitBlobSha(bytes);
                if (!string.Equals(actual, expectedSha, StringComparison.OrdinalIgnoreCase))
                    return StagerResult.Fail($"refetch of '{rel}' did not match the recorded SHA; re-install required");
                var localPath = Path.Combine(folder, rel);
                var parent = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                await File.WriteAllBytesAsync(localPath, bytes, ct).ConfigureAwait(false);
                newManifest[rel] = actual;
                written++;
            }
            catch (GitHubApiException ex)
            {
                return StagerResult.Fail($"could not re-download '{rel}': {ex.Message}");
            }
        }

        remote.FilesManifest = newManifest;
        remote.InstalledUtc = DateTime.UtcNow.ToString("o");
        try { state.Save(statePath); }
        catch (Exception ex) { return StagerResult.Fail($"healed files but state save failed: {ex.Message}"); }
        return StagerResult.Success($"re-downloaded all {written} file(s)", written, 0);
    }

    /// <summary>
    /// Rebuild the in-repo path (e.g. <c>content-mods/my-mod/apply.ps1</c>) from the
    /// recorded <see cref="RemoteState.FolderPathInRepo"/>. Falls back to a plain
    /// <c>content-mods/&lt;modId&gt;/&lt;rel&gt;</c> if the field is missing (pre-R3 state).
    /// </summary>
    private static string RepoJoin(RemoteState remote, string modIdHint, string rel)
    {
        var norm = rel.Replace(Path.DirectorySeparatorChar, '/').Replace('\\', '/');
        var folder = (remote.FolderPathInRepo ?? "").TrimEnd('/');
        if (string.IsNullOrEmpty(folder))
            folder = $"content-mods/{modIdHint}";
        return $"{folder}/{norm}";
    }

    private static string ShortSha(string sha) => string.IsNullOrEmpty(sha) ? "" : sha[..Math.Min(7, sha.Length)];
}
