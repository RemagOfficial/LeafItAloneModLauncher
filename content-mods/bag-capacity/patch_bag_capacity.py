#!/usr/bin/env python3
"""
patch_bag_capacity.py - the bag-capacity content mod's actual editor.

Rewrites the four `*BagCapacity` MonoBehaviour `capacity` int fields in
Leaf it Alone's coreassets metadata bundle. Reads TypeTrees (preserved across
this game's bundles despite IL2CPP metadata encryption), sets each `capacity`
to an explicit value (or vanilla x a scale factor, default 2.0), writes the
typetrees back, repacks
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


def patch(in_path, out_dir, scale=2.0, targets=None):
    """Patch the four bag capacities.

    targets: dict {SmallBagCapacity/MediumBagCapacity/BigBagCapacity/GiantBagCapacity -> int},
             used verbatim when given. Otherwise each vanilla value is multiplied by `scale`.
    The input bundle must still hold the vanilla values (revert first if it doesn't),
    so this always patches from a known baseline and never double-applies.
    """
    if not os.path.isfile(in_path):
        sys.exit(f"ERROR: input bundle not found: {in_path}")
    os.makedirs(out_dir, exist_ok=True)

    # Resolve the requested final values.
    if targets is not None:
        if set(targets) != set(EXPECTED):
            sys.exit(f"ERROR: targets must set exactly {sorted(EXPECTED)}; got {sorted(targets)}")
        want = {}
        for name, v in targets.items():
            v = int(v)
            if v <= 0:
                sys.exit(f"ERROR: target for {name} must be a positive int (got {v})")
            want[name] = v
    else:
        want = {name: int(round(orig * scale)) for name, orig in EXPECTED.items()}

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
            # Already modified (by this or another mod). Patch from a clean baseline.
            sys.exit(f"ERROR: {name} capacity is {orig}, not the expected vanilla "
                     f"{EXPECTED[name]}. Revert any prior mod first (revert.ps1 / "
                     f"uninstall-all.ps1) before re-applying.")
        new = want[name]
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
        "mode": "targets" if targets is not None else "scale",
        "scale": scale if targets is None else None,
        "changes": changes,
        "verified": True,
        "input_size": os.path.getsize(in_path),
        "output_size": os.path.getsize(out_path),
    }
    print(json.dumps(report, indent=2))
    return out_path


def main():
    ap = argparse.ArgumentParser(description="Set the four Leaf it Alone bag capacities (multiplier or explicit).")
    ap.add_argument("--in", dest="in_path", required=True, help="source .bundle (read-only)")
    ap.add_argument("--out-dir", dest="out_dir", help="dir to write the patched bundle into (required unless --verify-only)")
    ap.add_argument("--scale", type=float, default=2.0, help="multiplier vs vanilla 50/200/500/800 (default 2.0); ignored if any of --small/--medium/--big/--giant is given")
    ap.add_argument("--small", type=int, help="explicit SmallBagCapacity value")
    ap.add_argument("--medium", type=int, help="explicit MediumBagCapacity value")
    ap.add_argument("--big", type=int, help="explicit BigBagCapacity value")
    ap.add_argument("--giant", type=int, help="explicit GiantBagCapacity value")
    ap.add_argument("--verify-only", action="store_true", help="just print current capacities and exit")
    args = ap.parse_args()

    if args.verify_only:
        print(json.dumps(read_capacities(args.in_path), indent=2))
        return

    if not args.out_dir:
        ap.error("--out-dir is required unless --verify-only is set")

    explicit = [args.small, args.medium, args.big, args.giant]
    if any(v is not None for v in explicit):
        missing = [lbl for lbl, v in zip(("small", "medium", "big", "giant"), explicit) if v is None]
        if missing:
            ap.error("when giving explicit sizes you must give all of --small/--medium/--big/--giant "
                     "(missing: %s)" % ",".join(missing))
        targets = {
            "SmallBagCapacity": args.small,
            "MediumBagCapacity": args.medium,
            "BigBagCapacity": args.big,
            "GiantBagCapacity": args.giant,
        }
        patch(args.in_path, args.out_dir, targets=targets)
    else:
        patch(args.in_path, args.out_dir, scale=args.scale)


if __name__ == "__main__":
    main()
