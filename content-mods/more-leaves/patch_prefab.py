#!/usr/bin/env python3
"""
patch_prefab.py - scale the RUNTIME leaf-area template (coreprefabs_assets_actor).

The leaf layout component `Eternity.Gameplay.Leaves.Area` exists in TWO places per world:
  * the GAMEPLAY SCENE bundle (scenes_scenes_<W>) - patch_leaves.py scales this; it drives
    the VISUAL spawn + the completion-goal denominator (what you SEE and what you must hit);
  * the ACTOR PREFAB bundle (coreprefabs_assets_actor) - THIS file. The LeafArea_Hometown /
    _Castle / _Sakura runtime actors instantiate from this template, and its per-area
    leavesCount is what registers leaves as COLLECTABLE (pickable). Its layoutArea refs are
    stripped (it is the template, not the scene instance) but the counts are real.

Scaling the scene + areas but NOT the prefab was the pickup bug: extra leaves spawn and the
goal rises, but the prefab still tells the runtime area to register only the vanilla count
as pickable, so the extras are un-rakable ghosts. All four sources (bank positions, scene
layout, areas registry, prefab layout) must carry the same xN per world.

This bundle holds all THREE worlds' layout objects in one file, distinguished by their
top-level leavesCount, which equals the vanilla pool for that world:
    hometown 102400  |  castle 140288  |  sakura 260096
(apply.ps1 always reverts to pristine first, so those bases are what we read.) We scale each
object by ITS world's integer factor (default 1 = leave that object byte-identical): every
included non-zero area becomes round(orig*scale), the object's top leavesCount is recomputed
to the new sum, and everything else (boundsSize/boundsOffset/initialLeaves/references,
excludeFromInit entries, layoutArea refs) is preserved. Writes go through the proven
read_typetree -> save_typetree -> env.save(pack="original") path, then the OUTPUT is re-opened
and each world's invariant + per-area values are re-asserted before exiting 0.

Only ever reads --in, writes into --out-dir; never touches the game install.

Run with the pinned interpreter:  C:\\Python312\\python.exe patch_prefab.py --help
"""
import argparse
import glob
import json
import os
import sys

import UnityPy

# vanilla per-world top pools; a layout object is assigned to the world whose base it matches.
BASE = {"hometown": 102400, "castle": 140288, "sakura": 260096}


def _is_layout(tree):
    return (isinstance(tree, dict)
            and "initArgs" in tree and isinstance(tree.get("initArgs"), list)
            and "leavesCount" in tree)


def _world_of(top):
    top = int(top)
    for w, b in BASE.items():
        if abs(top - b) <= 1:            # tolerate a single rounding artifact
            return w
    return None


def _objects(env):
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
    """Per world object: its path_id, the top total, the initArgs sum + invariant. Used by
    --verify-only and the post-write check."""
    env = UnityPy.load(bundle_path)
    out = {}
    for obj, tree in _objects(env):
        args = tree["initArgs"]
        counts = [int(a.get("leavesCount", 0)) for a in args]
        top = int(tree["leavesCount"])
        out[str(obj.path_id)] = {
            "world": _world_of(top) or "(unknown)",
            "top": top,
            "sum": sum(counts),
            "invariant": sum(counts) == top,
            "areas": len(args),
        }
    return out


def patch(in_path, out_dir, scales):
    """scales: dict world -> int factor (>=1). Worlds at 1 are left untouched."""
    if not os.path.isfile(in_path):
        sys.exit(f"ERROR: input bundle not found: {in_path}")
    env = UnityPy.load(in_path)
    layouts = list(_objects(env))
    if not layouts:
        sys.exit(f"ERROR: no leaf-layout MonoBehaviour (initArgs+leavesCount) in "
                 f"{os.path.basename(in_path)}. Wrong bundle - refusing to patch.")

    changes = []
    for obj, tree in layouts:
        top = int(tree["leavesCount"])
        world = _world_of(top)
        if world is None:
            sys.exit(f"ERROR: a layout object (top={top}) matches no known world base "
                     f"{BASE}. Game may have updated - refusing.")
        sc = int(scales.get(world, 1))
        if sc <= 1:
            changes.append({"world": world, "scale": sc, "skipped": True})
            continue                                   # leave this object byte-identical
        args = tree["initArgs"]
        before = [int(a.get("leavesCount", 0)) for a in args]
        new_counts = []
        for a in args:
            lc = int(a.get("leavesCount", 0))
            if int(a.get("excludeFromInit", 0)) or lc == 0:
                new_counts.append(lc)
            else:
                new_counts.append(int(round(lc * sc)))
        for a, nc in zip(args, new_counts):
            a["leavesCount"] = nc
        tree["leavesCount"] = sum(new_counts)
        obj.save_typetree(tree)
        changes.append({"world": world, "scale": sc, "skipped": False,
                        "before_total": top, "after_total": sum(new_counts),
                        "before": before, "after": new_counts})

    os.makedirs(out_dir, exist_ok=True)
    env.save(pack="original", out_path=out_dir)
    produced = glob.glob(os.path.join(out_dir, "*.bundle"))
    if not produced:
        sys.exit("ERROR: UnityPy produced no .bundle in the output dir.")
    want = os.path.join(out_dir, os.path.splitext(os.path.basename(in_path))[0] + ".bundle")
    out_path = want if os.path.isfile(want) else produced[0]

    # Re-open the OUTPUT; every scaled world must satisfy top==sum with the expected new total.
    got = survey(out_path)
    for c in changes:
        if c.get("skipped"):
            continue
        # find the output object for this world
        for pid, info in got.items():
            if info["world"] != c["world"]:
                continue
            if not info["invariant"]:
                sys.exit(f"ERROR: verify failed for {c['world']}: top {info['top']} != sum "
                         f"{info['sum']} in the written bundle.")
            if info["top"] != c["after_total"]:
                sys.exit(f"ERROR: verify failed for {c['world']}: output top {info['top']} "
                         f"!= expected {c['after_total']}.")
    report = {"in": in_path, "out": out_path, "scales": scales, "changes": changes,
              "input_size": os.path.getsize(in_path), "output_size": os.path.getsize(out_path),
              "verified": True}
    print(json.dumps(report, indent=2))
    return out_path


def main():
    ap = argparse.ArgumentParser(
        description="Scale each world's leaf-area layout inside coreprefabs_assets_actor (per-world factors; 1 = leave untouched).")
    ap.add_argument("--in", dest="in_path", required=True, help="actor prefab .bundle (read-only)")
    ap.add_argument("--out-dir", dest="out_dir", help="dir to write the patched bundle (required unless --verify-only)")
    ap.add_argument("--scale-hometown", type=int, default=1, dest="sh")
    ap.add_argument("--scale-castle", type=int, default=1, dest="sc")
    ap.add_argument("--scale-sakura", type=int, default=1, dest="ss")
    ap.add_argument("--verify-only", action="store_true")
    args = ap.parse_args()

    if args.verify_only:
        print(json.dumps(survey(args.in_path), indent=2))
        return
    if not args.out_dir:
        ap.error("--out-dir is required unless --verify-only is set")
    patch(args.in_path, args.out_dir, {"hometown": args.sh, "castle": args.sc, "sakura": args.ss})


if __name__ == "__main__":
    main()
