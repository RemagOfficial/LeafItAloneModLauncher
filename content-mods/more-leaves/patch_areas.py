#!/usr/bin/env python3
"""
patch_areas.py - the more-leaves editor for the COLLECTABLE leaf count per area.

The three coupled leaf numbers we must scale TOGETHER for a winnable dense area are:
  * the scatter-position BANK (InitialLeaves_<World> in coreassets_assets_leaves) -> the
    positions leaves can be placed at; appended by patch_positions.py;
  * the SCENE layout budget (initArgs[].leavesCount + the top pool, in scenes_scenes_<W>)
    -> the VISUAL spawn count AND the completion-goal denominator; patch_leaves.py;
  * the AREAS collectable count (a top-level `leavesCount` on each NAMED-area MonoBehaviour
    in coreassets_assets_areas<,1,2>_<hash>) -> how many leaves are actually REGISTERED as
    PICKABLE. THIS file.

In vanilla all three are equal per area and each bundle's areas sum == the world bank count
(Hometown 102400, Castle 140288, Sakura 260096). Earlier we thought areas did nothing
(scaling areas ALONE with bank/scene vanilla added no leaves - the other limiters capped
it), but scaling bank+scene WITHOUT areas leaves the goal/visuals at xN while only the
vanilla count stays pickable -> at x10 most leaves cannot be collected. So areas MUST be
scaled in lockstep.

Each area object here is a plain MonoBehaviour with a single top-level integer
`leavesCount` (0 = interior/empty area). There is no parent pool to recompute; we just
scale every non-zero area by --scale (rounded, 0 stays 0) and keep everything else
(titleKey, progressRewards, references, isMetaArea) byte-faithful. Writes via the proven
read_typetree -> save_typetree -> env.save(pack="original") path (patch_leaves.py), then
RE-OPENS the output and asserts each area's new leavesCount == round(old*scale).

Only ever reads --in, writes into --out-dir; never touches the game install.

Run with the pinned interpreter:  C:\\Python312\\python.exe patch_areas.py --help
"""
import argparse
import glob
import json
import os
import sys

import UnityPy


def _is_area(tree):
    """An AREAS collectable component: a MonoBehaviour with a top-level integer
    leavesCount but WITHOUT the scene layout's initArgs list (that lives in the scene
    bundle, not here)."""
    return (isinstance(tree, dict)
            and "leavesCount" in tree
            and isinstance(tree.get("leavesCount"), int)
            and "initArgs" not in tree)


def _objects(env):
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        if _is_area(tree):
            yield obj, tree


def survey(bundle_path):
    """Per-area leavesCount + the running sum. Used by --verify-only and by the
    post-write check."""
    env = UnityPy.load(bundle_path)
    out = {}
    for obj, tree in _objects(env):
        out[str(obj.path_id)] = {
            "name": tree.get("m_Name", ""),
            "leavesCount": int(tree["leavesCount"]),
        }
    return out


def patch(in_path, out_dir, scale):
    if not os.path.isfile(in_path):
        sys.exit(f"ERROR: input bundle not found: {in_path}")
    if scale <= 0:
        sys.exit(f"ERROR: --scale must be > 0 (got {scale})")

    env = UnityPy.load(in_path)
    areas = list(_objects(env))
    if not areas:
        sys.exit(f"ERROR: no area collectable MonoBehaviours (top-level leavesCount) in "
                 f"{os.path.basename(in_path)}. Wrong bundle - refusing to patch.")

    plan = []
    old_sum = 0
    new_sum = 0
    for obj, tree in areas:
        before = int(tree["leavesCount"])
        old_sum += before
        after = before if before == 0 else int(round(before * scale))
        new_sum += after
        if after != before:
            tree["leavesCount"] = after
            obj.save_typetree(tree)
        plan.append({"path_id": str(obj.path_id), "name": tree.get("m_Name", ""),
                     "before": before, "after": after})

    os.makedirs(out_dir, exist_ok=True)
    env.save(pack="original", out_path=out_dir)
    produced = glob.glob(os.path.join(out_dir, "*.bundle"))
    if not produced:
        sys.exit("ERROR: UnityPy produced no .bundle in the output dir.")
    want_base = os.path.splitext(os.path.basename(in_path))[0] + ".bundle"
    out_path = os.path.join(out_dir, want_base)
    if not os.path.isfile(out_path):
        out_path = produced[0]

    # Re-open the OUTPUT and assert every area equals round(old*scale).
    got = survey(out_path)
    for p in plan:
        g = got.get(p["path_id"])
        if not g or g["leavesCount"] != p["after"]:
            sys.exit(f"ERROR: verify failed on area {p['name']}: output "
                     f"{g.get('leavesCount') if g else 'MISSING'} != expected {p['after']}.")

    report = {"in": in_path, "out": out_path, "scale": scale,
              "areas": len(plan), "old_sum": old_sum, "new_sum": new_sum,
              "plan": plan, "input_size": os.path.getsize(in_path),
              "output_size": os.path.getsize(out_path), "verified": True}
    print(json.dumps(report, indent=2))
    return out_path


def main():
    ap = argparse.ArgumentParser(
        description="Scale each named area's collectable leavesCount in a coreassets_assets_areas bundle.")
    ap.add_argument("--in", dest="in_path", required=True, help="areas .bundle (read-only)")
    ap.add_argument("--out-dir", dest="out_dir", help="dir to write the patched bundle (required unless --verify-only)")
    ap.add_argument("--scale", type=float, default=2.0, help="multiplier on each non-zero area's leavesCount (default 2.0)")
    ap.add_argument("--verify-only", action="store_true", help="just print the current area survey and exit")
    args = ap.parse_args()

    if args.verify_only:
        print(json.dumps(survey(args.in_path), indent=2))
        return
    if not args.out_dir:
        ap.error("--out-dir is required unless --verify-only is set")
    patch(args.in_path, args.out_dir, args.scale)


if __name__ == "__main__":
    main()
