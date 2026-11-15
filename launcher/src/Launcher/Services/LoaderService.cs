using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// Snapshot of the loader install: whether the loader file set is present in the game
/// folder with a manifest that agrees on its bytes, plus the version recorded when the
/// launcher last installed it. Read cheaply off disk (no subprocess) for the Dashboard.
/// </summary>
public sealed record LoaderInfo(bool Installed, string Version, string InstalledAt)
{
    public static LoaderInfo NotInstalled { get; } = new(false, "", "");
}

/// <summary>
/// Installs the real mod loader - <c>version.dll</c> (which is both the offline banner and
/// the CLR host entry point) plus its two mandatory managed assemblies - into the game
/// folder, fetching the file set from a source repo's <c>loader/</c> folder so it always
/// matches the mods published there.
///
/// <para>
/// The launcher's safe-base install runs with <c>-SkipOverlay</c>, so the framework does
/// NOT drop its banner-only <c>version.dll</c> and writes no <c>versionOverlay</c> record.
/// This service is the one that puts <c>version.dll</c> in place, so it must also CREATE
/// that <c>manifest.json</c> record (hashing the loader's own bytes) - content mods'
/// <c>Assert-SafePreconditions</c> re-hashes the installed <c>version.dll</c> against it and
/// refuses to patch a game whose overlay record disagrees. This mirrors exactly what the
/// developer script <c>tools/install_loader.ps1</c> does, adapted to a fetched file set.
/// </para>
/// </summary>
public sealed class LoaderService
{
    /// <summary>
    /// The loader's file set. <c>version.dll</c> is the injected entry point (and the offline
    /// banner); <c>LiaLoader.dll</c> / <c>LiaLoader.Api.dll</c> sit beside it and are host-
    /// mandatory (the native side binds them by assembly name). Used as the fallback when a
    /// source's <c>loader.json</c> declares no explicit <c>files</c> list.
    /// </summary>
    public static readonly IReadOnlyList<string> LoaderFileSet = new[]
    {
        "version.dll", "LiaLoader.dll", "LiaLoader.Api.dll",
    };

    private readonly GitHubApi _api;

    public LoaderService(GitHubApi api) => _api = api;

    /// <summary>The small stamp the launcher writes next to the manifest recording the loader it installed.</summary>
    public static string StampPath => Path.Combine(ApplyOrchestrator.SafeBaseRoot, "loader.json");

    /// <summary>
    /// Download the source's <c>loader/</c> files into <c>remote-mods/&lt;key&gt;/loader/</c>
    /// (verifying each against GitHub's git-blob SHA so a re-run only pulls changed bytes),
    /// copy the loader file set into <paramref name="gameDir"/>, then create/update the
    /// <c>manifest.json</c> <c>versionOverlay</c> record to hash the installed
    /// <c>version.dll</c>. Never touches the game while it is running. Returns a
    /// <see cref="StagerResult"/> the UI logs; never throws for an expected failure.
    /// </summary>
    public async Task<StagerResult> FetchAndInstallAsync(
        ModSource source,
        SourceSnapshot snapshot,
        string gameDir,
        string manifestPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var loader = snapshot.Loader;
        if (loader is null || !loader.Present)
            return StagerResult.Fail("the selected source has no loader/ folder - cannot install the mod loader from it.");
        if (!string.IsNullOrEmpty(loader.Error))
            return StagerResult.Fail("loader metadata problem: " + loader.Error);
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return StagerResult.Fail("game folder not found - install the safe-base first so the launcher knows where the game lives.");

        var wanted = loader.Dlls.Count > 0 ? loader.Dlls : LoaderFileSet;

        // ---- 1. stage every loader/ file into the per-source cache, git-blob-SHA verified ----
        progress?.Report($"fetching loader v{(string.IsNullOrEmpty(loader.Version) ? "?" : loader.Version)} from {source.Key}");
        var stageDir = Path.Combine(RepoLocator.RemoteModsRoot, source.Key, "loader");
        Directory.CreateDirectory(stageDir);
        var staged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in loader.Files.Where(f => !f.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            var name = f.Path.Split('/').Last();
            var local = Path.Combine(stageDir, name);
            await StageVerifiedAsync(snapshot.Owner, snapshot.Repo, snapshot.Sha, f, local, ct).ConfigureAwait(false);
            staged[name] = local;
        }

        // ---- 2. the loader file set must all be present before we touch the game ----
        foreach (var name in wanted)
        {
            if (!staged.TryGetValue(name, out _))
                return StagerResult.Fail($"the source's loader/ folder is missing {name} - its loader is incomplete.");
        }

        // ---- 3. refuse a running game (the loader DLLs are locked while hosted) ----
        if (ApplyOrchestrator.IsGameRunning())
            return StagerResult.Fail($"\"{ApplyOrchestrator.GameProcessName}\" is running - close it before installing the loader.");

        // ---- 4. copy the file set into the game folder, parking any displaced bytes ----
        foreach (var name in wanted)
        {
            ct.ThrowIfCancellationRequested();
            var src = staged[name];
            var dst = Path.Combine(gameDir, name);
            await CopyWithBackupAsync(src, dst, ct).ConfigureAwait(false);
        }

        // ---- 5. record the overlay in the manifest so content mods' preconditions pass ----
        var versionDll = Path.Combine(gameDir, "version.dll");
        var vbytes = await File.ReadAllBytesAsync(versionDll, ct).ConfigureAwait(false);
        var sha = Convert.ToHexString(SHA256.HashData(vbytes));   // uppercase, matches Get-FileHash .Hash
        RecordOverlayManifest(manifestPath, gameDir, versionDll, sha, vbytes.Length);

        // ---- 6. stamp the install so the Dashboard can report the version offline ----
        WriteStamp(loader.Version, sha);
        progress?.Report($"loader installed (v{loader.Version}, version.dll {vbytes.Length} bytes)");

        return StagerResult.Success($"installed mod loader v{loader.Version} ({wanted.Count} file(s))", wanted.Count, 0);
    }

    /// <summary>
    /// Read the loader state from disk: every file in <see cref="LoaderFileSet"/> present in
    /// the game folder AND the manifest's <c>versionOverlay.sourceDllSha256</c> agreeing with
    /// the <c>version.dll</c> bytes there. A banner-only overlay from a pre-loader install (no
    /// <c>LiaLoader.dll</c>) reports not-installed, so the combined install re-runs and upgrades it.
    /// </summary>
    public static LoaderInfo ReadInstalled(string? gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return LoaderInfo.NotInstalled;
            foreach (var name in LoaderFileSet)
                if (!File.Exists(Path.Combine(gameDir, name))) return LoaderInfo.NotInstalled;

            var versionDll = Path.Combine(gameDir, "version.dll");
            if (!ManifestOverlayMatches(SafeBaseService.ManifestPath, versionDll)) return LoaderInfo.NotInstalled;

            var stamp = ReadStamp();
            return new LoaderInfo(true, stamp?.Version ?? "", stamp?.InstalledAt ?? "");
        }
        catch
        {
            return LoaderInfo.NotInstalled;
        }
    }

    /// <summary>Download one staged file into <paramref name="local"/> unless it already matches its git-blob SHA.</summary>
    private async Task StageVerifiedAsync(
        string owner, string repo, string sha, RemoteFileEntry f, string local, CancellationToken ct)
    {
        if (File.Exists(local))
        {
            try
            {
                var existing = await File.ReadAllBytesAsync(local, ct).ConfigureAwait(false);
                if (string.Equals(GitHubApi.ComputeGitBlobSha(existing), f.Sha, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            catch { /* fall through and re-download */ }
        }
        var (bytes, _) = await _api.DownloadFileAsync(owner, repo, sha, f.Path, ct).ConfigureAwait(false);
        var actual = GitHubApi.ComputeGitBlobSha(bytes);
        if (!string.IsNullOrEmpty(f.Sha) && !string.Equals(actual, f.Sha, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{f.Path} downloaded with a different SHA than the listing advertised");
        var tmp = local + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
        if (File.Exists(local)) File.Replace(tmp, local, null);
        else File.Move(tmp, local);
    }

    /// <summary>Copy src over dst, parking any differing existing dst under the overlay backup folder by hash.</summary>
    private static async Task CopyWithBackupAsync(string src, string dst, CancellationToken ct)
    {
        if (File.Exists(dst))
        {
            var srcHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(src, ct).ConfigureAwait(false)));
            var dstHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(dst, ct).ConfigureAwait(false)));
            if (string.Equals(srcHash, dstHash, StringComparison.OrdinalIgnoreCase)) return;   // already current
            var backupDir = Path.Combine(ApplyOrchestrator.SafeBaseRoot, "backup", "overlay");
            Directory.CreateDirectory(backupDir);
            var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ");
            var leaf = Path.GetFileNameWithoutExtension(dst);
            var park = Path.Combine(backupDir, $"{leaf}.{stamp}.{dstHash[..Math.Min(8, dstHash.Length)]}.dll");
            File.Copy(dst, park, overwrite: true);
        }
        File.Copy(src, dst, overwrite: true);
    }

    /// <summary>
    /// Create/update the <c>manifest.json</c> <c>versionOverlay</c> record so it hashes the
    /// loader's <c>version.dll</c>. With <c>-SkipOverlay</c> the framework writes no such
    /// record, so this is what lets <c>Assert-SafePreconditions</c> (which compares the live
    /// <c>version.dll</c> hash against <c>sourceDllSha256</c>) and <c>uninstall.ps1</c> (which
    /// removes <c>version.dll</c> only when its hash matches) both treat the loader as ours.
    /// </summary>
    private static void RecordOverlayManifest(string manifestPath, string gameDir, string versionDll, string sha, long size)
    {
        JsonObject root;
        try
        {
            root = File.Exists(manifestPath)
                ? (JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject() ?? new JsonObject())
                : new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        var now = DateTime.UtcNow.ToString("o");
        root["versionOverlay"] = new JsonObject
        {
            ["gameFolder"] = gameDir,
            ["versionDllPath"] = versionDll,
            ["overlayIniPath"] = Path.Combine(gameDir, "LeafOverlay.ini"),
            ["sourceDllSize"] = size,
            ["sourceDllSha256"] = sha,
            ["installedAt"] = now,
        };

        var tmp = manifestPath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (File.Exists(manifestPath)) File.Replace(tmp, manifestPath, null);
        else File.Move(tmp, manifestPath);
    }

    /// <summary>True when the manifest's versionOverlay.sourceDllSha256 equals the live version.dll hash.</summary>
    private static bool ManifestOverlayMatches(string manifestPath, string versionDll)
    {
        try
        {
            if (!File.Exists(manifestPath) || !File.Exists(versionDll)) return false;
            var root = JsonNode.Parse(File.ReadAllText(manifestPath))?.AsObject();
            var recorded = root?["versionOverlay"]?["sourceDllSha256"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(recorded)) return false;
            var live = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(versionDll)));
            return string.Equals(recorded, live, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private sealed class LoaderStamp
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("sourceDllSha256")] public string SourceDllSha256 { get; set; } = "";
        [JsonPropertyName("installedAt")] public string InstalledAt { get; set; } = "";
    }

    private static void WriteStamp(string version, string sha)
    {
        try
        {
            var stamp = new LoaderStamp
            {
                Version = version,
                SourceDllSha256 = sha,
                InstalledAt = DateTime.UtcNow.ToString("o"),
            };
            var dir = Path.GetDirectoryName(StampPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StampPath, JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* the stamp is a nicety; a failed write never blocks a good install */ }
    }

    private static LoaderStamp? ReadStamp()
    {
        try
        {
            if (!File.Exists(StampPath)) return null;
            return JsonSerializer.Deserialize<LoaderStamp>(File.ReadAllText(StampPath));
        }
        catch
        {
            return null;
        }
    }
}
