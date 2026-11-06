using System.Collections.Generic;
using System.Linq;
using LeafItAlone.Launcher.Models;

namespace LeafItAlone.Launcher.Services;

/// <summary>
/// 2b mod conflict detection. Given the set of enabled mods, it looks at the
/// <c>patches[]</c> each author declared and reports every engine target that two
/// or more of them rewrite. The comparison key is the normalised
/// <c>(bundle, asset, field)</c> triple on <see cref="PatchTarget"/>, so a genuine
/// overlap (two mods setting the same leaf field in the scenes bundle) surfaces as
/// a warning while a field-level difference on a shared actor (bag-capacity's
/// capacity fields vs faster-bag-tying's <c>sealingTime</c>) correctly does not.
///
/// Pure and offline: no I/O, no network, no game access - it only reads the
/// in-memory manifests, which is exactly what the headless suite drives.
/// </summary>
public static class ConflictAnalyzer
{
    /// <summary>One shared target patched by 2+ enabled mods.</summary>
    public sealed record Conflict(PatchTarget Target, IReadOnlyList<string> ModNames)
    {
        /// <summary>The user-facing line, e.g. "field X on Y (bundle) is patched by both A and B - last-applied wins".</summary>
        public string Message
        {
            get
            {
                var names = ModNames.Count <= 2
                    ? string.Join(" and ", ModNames)
                    : string.Join(", ", ModNames.Take(ModNames.Count - 1)) + ", and " + ModNames[^1];
                return $"{Target.Describe()} is patched by both {names} - last-applied wins";
            }
        }
    }

    /// <summary>
    /// Analyse <paramref name="mods"/> (already filtered to the enabled set by the
    /// caller) and return one <see cref="Conflict"/> per triple claimed by more than
    /// one of them. Targets are reported in first-seen order and mods within a target
    /// in input order, so the warnings are stable across recomputes.
    /// </summary>
    public static IReadOnlyList<Conflict> Analyze(IEnumerable<ModInfo> mods)
    {
        // triple key -> (target descriptor, ordered distinct mod names)
        var byKey = new Dictionary<string, Bucket>();
        var order = new List<string>();

        foreach (var mod in mods)
        {
            foreach (var target in mod.Manifest.Patches)
            {
                if (target is null || !target.IsMeaningful) continue;
                var key = target.Key;
                if (!byKey.TryGetValue(key, out var bucket))
                {
                    bucket = new Bucket(target);
                    byKey[key] = bucket;
                    order.Add(key);
                }
                bucket.Mods.AddDistinct(mod.Name);
            }
        }

        var result = new List<Conflict>();
        foreach (var key in order)
        {
            var bucket = byKey[key];
            if (bucket.Mods.Count >= 2)
                result.Add(new Conflict(bucket.Target, bucket.Mods.ToArray()));
        }
        return result;
    }

    private sealed class Bucket
    {
        public Bucket(PatchTarget target) => Target = target;
        public PatchTarget Target { get; }
        public List<string> Mods { get; } = new();
    }

    private static void AddDistinct(this List<string> list, string value)
    {
        if (!list.Contains(value)) list.Add(value);
    }
}
