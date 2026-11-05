using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LeafItAlone.Launcher.Models;

namespace LeafItAlone.Launcher.Services;

/// <summary>Result of a discovery sweep: the valid mods plus any manifests that failed to read.</summary>
public sealed record DiscoveryResult(
    IReadOnlyList<ModInfo> Mods,
    IReadOnlyList<string> Errors);

/// <summary>
/// Reads every <c>content-mods/&lt;id&gt;/mod.json</c> into a
/// <see cref="ModInfo"/>. This is the data-driven core: the launcher's mod list is
/// entirely a function of the manifests on disk, so shipping a new mod needs no
/// code change (plan "Data model"). A broken manifest is reported and skipped
/// rather than taking down the whole list.
/// </summary>
public sealed class ModDiscovery
{
    // Case-insensitive + allow comments/trailing commas so hand-edited manifests
    // stay forgiving; the on-disk schema is camelCase but authors may vary.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Scan <paramref name="modsRoot"/> (a directory of mod folders) for manifests.</summary>
    public DiscoveryResult Discover(string modsRoot)
    {
        var mods = new List<ModInfo>();
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(modsRoot) || !Directory.Exists(modsRoot))
        {
            errors.Add($"mods root not found: {modsRoot}");
            return new DiscoveryResult(mods, errors);
        }

        foreach (var folder in Directory.EnumerateDirectories(modsRoot))
        {
            var manifestPath = Path.Combine(folder, "mod.json");
            if (!File.Exists(manifestPath)) continue;   // not a launcher-managed mod (yet)

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<ModManifest>(json, JsonOptions)
                    ?? throw new InvalidDataException("mod.json is empty");

                if (string.IsNullOrWhiteSpace(manifest.Id))
                    manifest.Id = Path.GetFileName(folder);   // fall back to folder name

                if (string.IsNullOrWhiteSpace(manifest.Apply.Script))
                    throw new InvalidDataException("apply.script is required");

                mods.Add(new ModInfo(manifest, folder, manifestPath));
            }
            catch (Exception ex)
            {
                errors.Add($"{manifestPath}: {ex.Message}");
            }
        }

        var sorted = mods
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DiscoveryResult(sorted, errors);
    }
}
