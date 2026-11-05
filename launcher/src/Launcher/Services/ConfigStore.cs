using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// Reads and writes a mod's live config JSON (the <c>configPath</c> from its
/// manifest, e.g. <c>%LOCALAPPDATA%\LeafItAloneSafe\bag-capacity.json</c>).
///
/// <para>
/// Two hard rules from the plan (L2):
///  * <b>Preserve unknown keys.</b> The manifest only <em>describes</em> the keys
///    the launcher edits; everything else (<c>_comment</c>, <c>_mode_notes</c>,
///    <c>_applyHelper</c>, …) must survive a round trip untouched, because
///    <c>apply.ps1</c> and the overlay rely on them.
///  * <b>Address by dotted path.</b> A field key like <c>sizes.small</c> reads/writes
///    the nested <c>sizes</c> object, creating it if absent.
/// </para>
///
/// The file is the same one <c>apply.ps1</c> seeds and reads, so a launcher edit is
/// picked up unchanged by the existing engine.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Keep the document human-editable; System.Text.Json already preserves key
        // order within a JsonObject, so comments we don't understand round-trip by
        // staying as ordinary members (JSON has no comment node, see Read below).
    };

    /// <summary>Parse the live config, or return an empty object if the file is missing/blank.</summary>
    public static JsonObject Load(string path)
    {
        if (!File.Exists(path)) return new JsonObject();
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            return JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            // A hand-broken config shouldn't crash the editor. Treat as empty but let
            // the caller decide (Save will then drop the bad content) — surfaced via
            // the returned empty object and logged by the VM.
            return new JsonObject();
        }
    }

    /// <summary>Write the config as indented UTF-8 (no BOM; PowerShell's ConvertFrom-Json reads it fine).</summary>
    public static void Save(string path, JsonObject root)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, root.ToJsonString(WriteOptions), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>Fetch a value by dotted path (<c>sizes.small</c>), or null if any segment is missing.</summary>
    public static JsonNode? GetByPath(JsonObject root, string dottedKey)
    {
        var parts = dottedKey.Split('.');
        JsonNode? node = root;
        foreach (var part in parts)
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(part, out var next)) node = next;
            else return null;
        }
        return node;
    }

    /// <summary>
    /// Set a value by dotted path, creating intermediate objects as needed. A
    /// non-object encountered mid-path is replaced by a fresh object.
    /// </summary>
    public static void SetByPath(JsonObject root, string dottedKey, JsonNode? value)
    {
        var parts = dottedKey.Split('.');
        JsonObject current = root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var part = parts[i];
            if (current.TryGetPropertyValue(part, out var existing) && existing is JsonObject asObj)
            {
                current = asObj;
            }
            else
            {
                var created = new JsonObject();
                current[part] = created;
                current = created;
            }
        }
        current[parts[^1]] = value;
    }

    /// <summary>
    /// SHA-256 (lowercase hex) of the file's bytes — the "pending" fingerprint.
    /// A missing file hashes as empty (a stable sentinel), so "config edited but
    /// never written" still compares cleanly against <c>lastAppliedConfigSha</c>.
    /// </summary>
    public static string ComputeSha(string path)
    {
        if (!File.Exists(path)) return ShaOf(Array.Empty<byte>());
        return ShaOf(File.ReadAllBytes(path));
    }

    private static string ShaOf(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
