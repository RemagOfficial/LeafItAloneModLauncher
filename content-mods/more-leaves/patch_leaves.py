#!/usr/bin/env python3
"""
patch_leaves.py - the more-leaves content mod's actual editor (scene-targeted).

What actually seeds the leaves on screen is a `Eternity.Gameplay.Leaves.Area`
layout MonoBehaviour baked into each GAMEPLAY SCENE bundle (scenes_scenes_castle
/ _hometown / _sakura). Its typetree carries:
  * a top-level integer `leavesCount`  = the leaf pool / budget for the scene, and
  * `initArgs`: a list of one entry per area, each
        { excludeFromInit:int, layoutArea:{...ref...}, leavesCount:int }
    where `leavesCount` is how many leaves are scattered into that area at load.
Recon (MODLOG) proved the invariant  top-level leavesCount == sum(initArgs[].leavesCount)
for every scene, so scaling each area and recomputing the total keeps the pool
consistent (the renderer's buffer grows with it, otherwise the extra leaves clamp).

NOTE: the `coreassets_assets_areas*` MonoBehaviours ALSO have a `leavesCount`, but
patching those has NO in-game effect (verified: 4x on disk, same leaves) - they are
metadata, not the spawner. And `coreprefabs_assets_actor` holds the same layout
component but with every `layoutArea` ref stripped to 0 (the prefab template, not a
runtime instance). The scene bundle is the authoritative copy, so this editor only
touches scene bundles.

This script scales every included area's leavesCount by --scale (rounded), leaves
`excludeFromInit` entries and 0-count areas untouched, recomputes the scene total as
the new sum, writes the typetrees back, repacks the bundle into --out-dir, then
RE-OPENS the output and re-asserts each value AND the sum invariant before exiting 0.
A non-zero exit means nothing usable was written and the caller must leave the game
folder untouched.

It only ever reads the --in bundle and writes into --out-dir; it never touches the
game install. apply.ps1 handles Backup-Original + the atomic replace + the catalog
CRC null + Register-Mod via the safe-base SDK, and reverts to a pristine baseline
before re-applying, so scaling what it reads is safe and idempotent.

Run with the pinned interpreter (UnityPy's native lz4/brotli deps segfault on
Python 3.14):  C:\\Python312\\python.exe patch_leaves.py --help
"""
import argparse
import glob
import json
import os
import sys

import UnityPy


def _is_layout(tree):
    """True if this MonoBehaviour typetree is a leaf-layout component we can edit."""
    return (isinstance(tree, dict)
            and "initArgs" in tree
            and isinstance(tree.get("initArgs"), list)
            and "leavesCount" in tree)


def _objects(env):
    """Yield (obj, tree) for every leaf-layout MonoBehaviour in the bundle."""
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        if _is_layout(tree):
            yield obj, tree


def survey(bundle_path):
    """Read-only summary: per layout object, the top total, the initArgs counts and
    the running sum. Used by --verify-only to prove the patch landed / learn state."""
    env = UnityPy.load(bundle_path)
    out = {}
    for obj, tree in _objects(env):
        args = tree["initArgs"]
        counts = [int(a.get("leavesCount", 0)) for a in args]
        s = sum(counts)
        out[str(obj.path_id)] = {
            "top": int(tree["leavesCount"]),
            "sum": s,
            "invariant": s == int(tree["leavesCount"]),
            "areas": len(args),
            "excluded": sum(1 for a in args if int(a.get("excludeFromInit", 0))),
            "counts": counts,
        }
    return out


def _scaled_counts(args, scale):
    """Return (new_counts, expected_total). Excluded entries and 0 areas keep their
    value; every other area becomes round(orig*scale). Total is the new sum so the
    pool matches the per-area spawn budget."""
    new_counts = []
    for a in args:
        lc = int(a.get("leavesCount", 0))
        if int(a.get("excludeFromInit", 0)) or lc == 0:
            new_counts.append(lc)                      # excluded / empty: unchanged
        else:
            new_counts.append(int(round(lc * scale)))
    return new_counts, sum(new_counts)


def _zeroed_counts(args, current_pool):
    """Negative-test helper: return (new_counts, pool_to_keep). Every INCLUDED, non-zero
    area is zeroed; excluded/already-0 areas keep their value. Crucially the top-level
    POOL is returned unchanged on purpose - this isolates whether `initArgs` is the
    spawn seed (pool full, budget 0 => if the lever is right the leaves DISAPPEAR), and
    avoids a pool of 0 that could leave the renderer with no buffer to prove it."""
    new_counts = [0 if (not int(a.get("excludeFromInit", 0)) and int(a.get("leavesCount", 0)))
                  else int(a.get("leavesCount", 0)) for a in args]
    return new_counts, int(current_pool)


def patch(in_path, out_dir, scale=2.0, zero=False):
    if not os.path.isfile(in_path):
        sys.exit(f"ERROR: input bundle not found: {in_path}")
    if not zero and scale <= 0:
        sys.exit(f"ERROR: --scale must be > 0 (got {scale})")

    env = UnityPy.load(in_path)
    layouts = list(_objects(env))
    if not layouts:
        sys.exit(f"ERROR: no leaf-layout MonoBehaviour (initArgs+leavesCount) in "
                 f"{os.path.basename(in_path)}. Wrong bundle - refusing to patch.")

    changes = []
    for obj, tree in layouts:
        args = tree["initArgs"]
        before = [int(a.get("leavesCount", 0)) for a in args]
        if zero:
            new_counts, pool_to_keep = _zeroed_counts(args, tree["leavesCount"])  # pool kept at current
        else:
            new_counts, total = _scaled_counts(args, scale)
            pool_to_keep = total                              # pool follows the new sum
        for a, nc in zip(args, new_counts):
            a["leavesCount"] = nc
        tree["leavesCount"] = pool_to_keep
        obj.save_typetree(tree)
        changes.append({
            "path_id": str(obj.path_id),
            "top_to": int(pool_to_keep),
            "areas": len(args),
            "before": before,
            "after": new_counts,
        })

    # pack="original" reuses each block's original compression so the output stays
    # close to vanilla (a smaller, cleaner delta than an uncompressed repack).
    os.makedirs(out_dir, exist_ok=True)
    env.save(pack="original", out_path=out_dir)

    produced = glob.glob(os.path.join(out_dir, "*.bundle"))
    if not produced:
        sys.exit("ERROR: UnityPy produced no .bundle in the output dir.")
    want_base = os.path.splitext(os.path.basename(in_path))[0] + ".bundle"
    out_path = os.path.join(out_dir, want_base)
    if not os.path.isfile(out_path):
        out_path = produced[0]

    # Post-write verification: re-open the OUTPUT and assert each object's values (and,
    # in scale mode, the sum invariant) against what we intended to write. In zero mode
    # the invariant is expected to break (budget 0, pool kept), so we only check values.
    check = survey(out_path)
    if len(check) != len(changes):
        sys.exit(f"ERROR: output has {len(check)} layout objects, expected {len(changes)}.")
    for ch in changes:
        got = check.get(ch["path_id"])
        if not got:
            sys.exit(f"ERROR: layout object {ch['path_id']} missing from the output bundle.")
        if got["counts"] != ch["after"]:
            sys.exit(f"ERROR: verify failed on object {ch['path_id']}: output counts "
                     f"{got['counts']} != expected {ch['after']}. Output NOT applied.")
        if got["top"] != ch["top_to"]:
            sys.exit(f"ERROR: verify failed on object {ch['path_id']}: top={got['top']} "
                     f"(expected {ch['top_to']}).")
        if not zero and not got["invariant"]:
            sys.exit(f"ERROR: verify failed on object {ch['path_id']}: sum={got['sum']} "
                     f"!= top={got['top']}; scale-mode invariant broken.")

    mode = "zero" if zero else "scale"
    report = {
        "in": in_path,
        "out": out_path,
        "mode": mode,
        "scale": None if zero else scale,
        "objects": len(changes),
        "changes": changes,
        "verified": True,
        "input_size": os.path.getsize(in_path),
        "output_size": os.path.getsize(out_path),
    }
    print(json.dumps(report, indent=2))
    return out_path


def main():
    ap = argparse.ArgumentParser(
        description="Scale per-area leavesCount (initArgs) + recompute the scene total in one scene bundle.")
    ap.add_argument("--in", dest="in_path", required=True, help="source scene .bundle (read-only)")
    ap.add_argument("--out-dir", dest="out_dir", help="dir to write the patched bundle into (required unless --verify-only)")
    ap.add_argument("--scale", type=float, default=2.0, help="multiplier on each included area's leavesCount (default 2.0); excluded/0 areas stay")
    ap.add_argument("--zero", action="store_true", help="NEGATIVE TEST: set every included area's leavesCount to 0 and keep the top pool, to prove whether initArgs drives spawning")
    ap.add_argument("--verify-only", action="store_true", help="just print the current layout survey JSON and exit")
    args = ap.parse_args()

    if args.verify_only:
        print(json.dumps(survey(args.in_path), indent=2))
        return

    if not args.out_dir:
        ap.error("--out-dir is required unless --verify-only is set")

    patch(args.in_path, args.out_dir, scale=args.scale, zero=args.zero)


if __name__ == "__main__":
    main()
