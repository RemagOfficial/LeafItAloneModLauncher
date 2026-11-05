#!/usr/bin/env python3
"""
patch_bag_capacity.py - the bag-capacity content mod's actual editor.

Rewrites the four `*BagCapacity` MonoBehaviour `capacity` int fields in
Leaf it Alone's coreassets metadata bundle. Reads TypeTrees (preserved across
this game's bundles despite IL2CPP metadata encryption), multiplies each
`capacity` by a scale factor (default 2.0), writes the typetrees back, repacks
the bundle into --out-dir, then RE-OPENS the output and verifies the new values
before exiting 0. A non-zero exit means nothing was written and the caller
should leave the game folder untouched.

This script only ever reads the --in bundle and writes into --out-dir. It does
NOT touch the game install; apply.ps1 handles Backup-Original + the atomic
replace + Register-Mod via the safe-base SDK.

Run with the pinned interpreter (UnityPy's native lz4/brotli deps segfault on
Python 3.14):  C:\\Python312\\python.exe patch_bag_capacity.py --help
"""
import argparse
import glob
import json
import os
import sys

import UnityPy

# m_Name -> expected vanilla capacity. A sanity anchor: if the bundle the mod
# is applied to does not contain exactly these, we refuse (wrong bundle / game
# already updated / another mod in the way).
EXPECTED = {
    "SmallBagCapacity": 50,
    "MediumBagCapacity": 200,
    "BigBagCapacity": 500,
    "GiantBagCapacity": 800,
}


def read_capacities(bundle_path):
    """Return {m_Name: capacity} for every *BagCapacity MonoBehaviour."""
    env = UnityPy.load(bundle_path)
    found = {}
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        tree = obj.read_typetree()
        name = tree.get("m_Name")
        if isinstance(name, str) and name.endswith("BagCapacity") and "capacity" in tree:
            found[name] = int(tree["capacity"])
    return found


def patch(in_path, out_dir, scale):
    if not os.path.isfile(in_path):
        sys.exit(f"ERROR: input bundle not found: {in_path}")
    os.makedirs(out_dir, exist_ok=True)

    # Pre-flight: the input must look like the vanilla bundle we expect.
    before = read_capacities(in_path)
    missing = [n for n in EXPECTED if n not in before]
    if missing:
        sys.exit(f"ERROR: expected BagCapacity objects not present: {missing}. "
                 f"Refusing to patch an unexpected bundle.")

    env = UnityPy.load(in_path)
    changes = []
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        tree = obj.read_typetree()
        name = tree.get("m_Name")
        if not (isinstance(name, str) and name in EXPECTED):
            continue
        orig = int(tree["capacity"])
        if orig != EXPECTED[name]:
            # Already modified (by this or another mod). Do not double-scale.
            sys.exit(f"ERROR: {name} capacity is {orig}, not the expected vanilla "
                     f"{EXPECTED[name]}. Revert any prior mod first (revert.ps1 / "
                     f"uninstall-all.ps1) before re-applying.")
        new = int(round(orig * scale))
        tree["capacity"] = new
        obj.save_typetree(tree)
        changes.append({"name": name, "from": orig, "to": new})

    if len(changes) != len(EXPECTED):
        sys.exit(f"ERROR: patched {len(changes)} of {len(EXPECTED)} expected objects.")

    # UnityPy writes the container into out_path keeping its name.
    # pack="original" reuses each block's original compression so the output
    # stays close to vanilla (a smaller, cleaner delta than an uncompressed
    # repack). out_path must already exist; env.save() does not mkdir.
    os.makedirs(out_dir, exist_ok=True)
    env.save(pack="original", out_path=out_dir)

    # Locate the produced bundle (prefer the same basename as the input).
    produced = glob.glob(os.path.join(out_dir, "*.bundle"))
    if not produced:
        sys.exit("ERROR: UnityPy produced no .bundle in the output dir.")
    want_base = os.path.splitext(os.path.basename(in_path))[0] + ".bundle"
    out_path = os.path.join(out_dir, want_base)
    if not os.path.isfile(out_path):
        out_path = produced[0]  # fall back to whatever was written

    # Post-write verification: re-open the OUTPUT and assert the new values.
    after = read_capacities(out_path)
    expected_after = {c["name"]: c["to"] for c in changes}
    for n, v in expected_after.items():
        if after.get(n) != v:
            sys.exit(f"ERROR: verify failed for {n}: output has {after.get(n)}, "
                     f"expected {v}. Output NOT applied.")

    report = {
        "in": in_path,
        "out": out_path,
        "scale": scale,
        "changes": changes,
        "verified": True,
        "input_size": os.path.getsize(in_path),
        "output_size": os.path.getsize(out_path),
    }
    print(json.dumps(report, indent=2))
    return out_path


def main():
    ap = argparse.ArgumentParser(description="Multiply the four Leaf it Alone bag capacities.")
    ap.add_argument("--in", dest="in_path", required=True, help="source .bundle (read-only)")
    ap.add_argument("--out-dir", dest="out_dir", help="dir to write the patched bundle into (required unless --verify-only)")
    ap.add_argument("--scale", type=float, default=2.0, help="multiplier (default 2.0)")
    ap.add_argument("--verify-only", action="store_true", help="just print current capacities and exit")
    args = ap.parse_args()

    if args.verify_only:
        print(json.dumps(read_capacities(args.in_path), indent=2))
        return

    if not args.out_dir:
        ap.error("--out-dir is required unless --verify-only is set")
    patch(args.in_path, args.out_dir, args.scale)


if __name__ == "__main__":
    main()
