using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace LeafItAlone.Launcher.Models;

/// <summary>
/// A mod's <c>content-mods/&lt;id&gt;/mod.json</c> manifest (plan "Data model").
/// This is the whole contract between a mod and the launcher: it names the mod,
/// declares the config editor schema, tells the launcher how to apply/revert it
/// (by shelling out to the existing validated scripts), and lists its runtime
/// prerequisites. Adding a mod means authoring a manifest, never writing app UI.
///
/// Field names here map to the JSON via <see cref="JsonPropertyNameAttribute"/>
/// so the on-disk manifest stays snake_case-friendly and stable.
/// </summary>
public sealed class ModManifest
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";

    /// <summary>Optional link for credits / more info.</summary>
    [JsonPropertyName("homepage")] public string? Homepage { get; set; }

    /// <summary>Lowest <see cref="Order"/> applies first; ties keep discovery order.</summary>
    [JsonPropertyName("order")] public int Order { get; set; }

    /// <summary>
    /// The live value store, e.g. <c>%LOCALAPPDATA%\LeafItAloneSafe\bag-capacity.json</c>.
    /// Tokens (<c>%LOCALAPPDATA%</c> etc.) are expanded by <see cref="Services.EnvPaths"/>.
    /// The manifest only <em>describes</em> this file; the launcher preserves any keys
    /// the schema does not mention (<c>_comment</c>, <c>_applyHelper</c>, ...).
    /// </summary>
    [JsonPropertyName("configPath")] public string ConfigPath { get; set; } = "";

    [JsonPropertyName("config")] public ConfigSchema Config { get; set; } = new();

    [JsonPropertyName("apply")] public CommandSpec Apply { get; set; } = new();
    [JsonPropertyName("revert")] public CommandSpec Revert { get; set; } = new();

    [JsonPropertyName("requires")] public Requires Requires { get; set; } = new();

    /// <summary>
    /// 2b conflict detection: the engine assets this mod rewrites, declared by the
    /// author so the launcher can warn before a risky combine. Each entry is a
    /// <see cref="PatchTarget"/> triple <c>(bundle, asset, field)</c>. Two enabled
    /// mods that name the <em>same</em> triple collide ("last-applied wins"); a
    /// different <c>field</c> on the same actor/bundle does NOT (so bag-capacity and
    /// faster-bag-tying, both touching the bag actors but different fields, are
    /// correctly independent). Empty means "undeclared" — the mod simply never
    /// participates in a conflict warning, exactly like pre-2b manifests.
    /// </summary>
    [JsonPropertyName("patches")] public List<PatchTarget> Patches { get; set; } = new();

    /// <summary>
    /// Mod-sources (2a R1): optional provenance fields. Third-party mod authors ship
    /// these in their <c>mod.json</c> so the launcher can show a compat hint when the
    /// mod is listed from a source repo. They never block install; the launcher only
    /// reads them for UI and for the trust modal. Empty string means "unspecified",
    /// which the browser treats as "compatible with anything at this SHA".
    /// </summary>
    [JsonPropertyName("sourceRepo")] public string SourceRepo { get; set; } = "";

    /// <summary>Minimum launcher version this mod was tested against, e.g. "1.1.0".</summary>
    [JsonPropertyName("minLauncherVersion")] public string MinLauncherVersion { get; set; } = "";

    /// <summary>Game version string this mod was authored against, e.g. "1.1.7".</summary>
    [JsonPropertyName("gameVersion")] public string GameVersion { get; set; } = "";
}

/// <summary>The <c>config</c> block: a flat list of editor fields (plan "config: a tiny schema list").</summary>
public sealed class ConfigSchema
{
    [JsonPropertyName("fields")] public List<ConfigField> Fields { get; set; } = new();
}

/// <summary>
/// One editable field. <see cref="Key"/> is a dotted path into the live JSON
/// (<c>multiplier</c>, <c>sizes.small</c>), so nested objects address cleanly.
/// </summary>
public sealed class ConfigField
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    /// <summary>One of: int | float | bool | enum | text (see <see cref="FieldTypes"/>).</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = FieldTypes.Text;

    [JsonPropertyName("min")] public double? Min { get; set; }
    [JsonPropertyName("max")] public double? Max { get; set; }
    [JsonPropertyName("step")] public double? Step { get; set; }

    /// <summary>Scalar fallback (string/number/bool) when the live config lacks this key.</summary>
    [JsonPropertyName("default")] public JsonNode? Default { get; set; }
    [JsonPropertyName("help")] public string? Help { get; set; }

    /// <summary>Only used when <see cref="Type"/> is <c>enum</c>.</summary>
    [JsonPropertyName("options")] public List<string> Options { get; set; } = new();

    /// <summary>Optional visibility gate, e.g. show <c>multiplier</c> only when <c>mode==multiplier</c>.</summary>
    [JsonPropertyName("when")] public WhenCondition? When { get; set; }
}

/// <summary>A conditional-visibility rule referencing another field's value.</summary>
public sealed class WhenCondition
{
    [JsonPropertyName("key")] public string Key { get; set; } = "";

    /// <summary>The value the referenced <see cref="Key"/> must equal for the field to show.</summary>
    [JsonPropertyName("equals")] public JsonNode? EqualsValue { get; set; }
}

/// <summary>
/// A command template relative to the mod folder. <see cref="Script"/> is the
/// PowerShell file (e.g. <c>apply.ps1</c>); <see cref="Args"/> are default
/// arguments. The launcher substitutes engine paths (the bundled <c>-Python</c>)
/// and always runs via the subprocess runner.
/// </summary>
public sealed class CommandSpec
{
    [JsonPropertyName("script")] public string Script { get; set; } = "";
    [JsonPropertyName("args")] public List<string> Args { get; set; } = new();
}

/// <summary>The <c>requires</c> block: preconditions the launcher asserts before applying.</summary>
public sealed class Requires
{
    [JsonPropertyName("safeBase")] public bool SafeBase { get; set; }
    [JsonPropertyName("gameNotRunning")] public bool GameNotRunning { get; set; }
    [JsonPropertyName("python")] public bool Python { get; set; }
}

/// <summary>
/// 2b: one declared patch target — a coarse, human-readable pointer at an engine
/// asset the mod rewrites. All parts are optional strings so a mod can be as
/// specific as it likes (a full <c>bundle/asset/field</c>) or leave a part blank
/// when it does not apply (e.g. a whole-bundle swap has no single <c>field</c>).
/// The launcher only compares the normalised <see cref="Key"/> triple, so two mods
/// that spell the same target identically collide and everything else does not.
/// </summary>
public sealed class PatchTarget
{
    /// <summary>The Unity addressable bundle (e.g. <c>coreassets.assets</c>, <c>scenes</c>, <c>leaves</c>).</summary>
    [JsonPropertyName("bundle")] public string Bundle { get; set; } = "";

    /// <summary>The asset / object inside the bundle (e.g. an actor or scene type name).</summary>
    [JsonPropertyName("asset")] public string Asset { get; set; } = "";

    /// <summary>The specific field on that asset (e.g. <c>LeafBagBehaviourSettings.sealingTime</c>).</summary>
    [JsonPropertyName("field")] public string Field { get; set; } = "";

    /// <summary>
    /// The normalised comparison key. Case- and whitespace-insensitive so authors
    /// cannot accidentally dodge a collision by capitalising differently; a blank
    /// part stays blank (it is still part of the tuple, keeping
    /// <c>bundle+asset+""</c> distinct from <c>bundle+asset+field</c>).
    /// </summary>
    public string Key =>
        string.Join("\u241F",
            (Bundle ?? "").Trim().ToLowerInvariant(),
            (Asset ?? "").Trim().ToLowerInvariant(),
            (Field ?? "").Trim().ToLowerInvariant());

    /// <summary>True when at least one part is populated (a wholly-empty entry is ignored).</summary>
    public bool IsMeaningful =>
        !string.IsNullOrWhiteSpace(Bundle) || !string.IsNullOrWhiteSpace(Asset) || !string.IsNullOrWhiteSpace(Field);

    /// <summary>Human-readable form for the warning text ("field in asset (bundle)").</summary>
    public string Describe()
    {
        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrWhiteSpace(Field)) parts.Add(Field.Trim());
        if (!string.IsNullOrWhiteSpace(Asset)) parts.Add("on " + Asset.Trim());
        if (!string.IsNullOrWhiteSpace(Bundle)) parts.Add("in " + Bundle.Trim());
        return parts.Count == 0 ? "(unspecified target)" : string.Join(" ", parts);
    }
}

/// <summary>String constants for the supported field <c>type</c> values.</summary>
public static class FieldTypes
{
    public const string Int = "int";
    public const string Float = "float";
    public const string Bool = "bool";
    public const string Enum = "enum";
    public const string Text = "text";
}
