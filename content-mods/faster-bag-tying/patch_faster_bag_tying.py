#!/usr/bin/env python3
"""
patch_faster_bag_tying.py - the faster-bag-tying content mod's editor.

Rewrites the `sealingTime` float on each Leaf it Alone bag actor's
`LeafBagBehaviourSettings` (a [SerializeReference] in the coreassets ACTORS
bundle), scaling the vanilla per-bag tying time by a multiplier (default 0.5 =
half the time = 2x faster) or setting each bag's seconds explicitly. Optionally
scales `emptyingDuration` by the same per-bag ratio. Reads TypeTrees (preserved
across this game's bundles despite IL2CPP metadata encryption), writes them
back, repacks into --out-dir, then RE-OPENS the output and verifies the new
values before exiting 0. A non-zero exit means nothing was written.

This script only ever reads the --in bundle and writes into --out-dir. It does
NOT touch the game install; apply.ps1 handles Backup-Original + the atomic
replace + Register-Mod via the safe-base SDK.

Vanilla anchors (from RE, see MODLOG): the four bag actors are
Small/Medium/Big/GiantLeafBag, each with a LeafBagBehaviourSettings RefId holding
sealingTime (tying) and emptyingDuration (dumping):
    Small 1.0 / 0.8   Medium 1.35 / 1.1   Big 1.7 / 1.4   Giant 1.8 / 1.5
The in-game "faster bag tying" upgrades are percentage-based (-20/-20/-20/-15%)
stat modifiers on top of these; this mod lowers the BASE so tying is faster
without buying the upgrades.

Run with the pinned interpreter (UnityPy's native lz4/brotli deps segfault on
Python 3.14):  C:\\Python312\\python.exe patch_faster_bag_tying.py --help
"""
import argparse
import glob
import json
import os
import sys

import UnityPy

# m_Name -> (vanilla sealingTime, vanilla emptyingDuration). A sanity anchor: if
# the bundle does not contain exactly these, we refuse (wrong bundle / game
# already updated / another mod in the way).
EXPECTED = {
    "SmallLeafBag":  (1.0,  0.8),
    "MediumLeafBag": (1.35, 1.1),
    "BigLeafBag":    (1.7,  1.4),
    "GiantLeafBag":  (1.8,  1.5),
}
TOL = 1e-4  # float32 comparison tolerance for the vanilla pre-flight


def _close(a, b):
    return abs(float(a) - float(b)) < TOL


def _bag_settings(tree):
    """Return the LeafBagBehaviourSettings `data` dict from a bag actor tree."""
    for r in tree.get("references", {}).get("RefIds", []):
        if r.get("type", {}).get("class") == "LeafBagBehaviourSettings":
            return r.get("data")
    return None


def read_times(bundle_path):
    """Return {m_Name: (sealingTime, emptyingDuration)} for the four bag actors."""
    env = UnityPy.load(bundle_path)
    found = {}
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        tree = obj.read_typetree()
        name = tree.get("m_Name") if isinstance(tree, dict) else None
        if isinstance(name, str) and name in EXPECTED:
            data = _bag_settings(tree)
            if data and "sealingTime" in data:
                found[name] = (float(data["sealingTime"]),
                               float(data.get("emptyingDuration", 0.0)))
    return found


def patch(in_path, out_dir, scale=0.5, targets=None, also_emptying=False):
    """Scale the four bag sealingTimes (and optionally emptyingDurations).

    targets: dict {SmallLeafBag/MediumLeafBag/BigLeafBag/GiantLeafBag -> seconds},
             used verbatim when given. Otherwise vanilla sealingTime x `scale`.
    The input bundle must still hold the vanilla values (revert first if it
    doesn't), so this always patches from a known baseline and never double-applies.
    """
    if not os.path.isfile(in_path):
        sys.exit(f"ERROR: input bundle not found: {in_path}")
    os.makedirs(out_dir, exist_ok=True)

    # Resolve the requested final sealing seconds.
    if targets is not None:
        if set(targets) != set(EXPECTED):
            sys.exit(f"ERROR: targets must set exactly {sorted(EXPECTED)}; got {sorted(targets)}")
        want = {}
        for name, v in targets.items():
            v = float(v)
            if v <= 0:
                sys.exit(f"ERROR: target sealingTime for {name} must be a positive number (got {v})")
            want[name] = v
    else:
        if scale <= 0 or scale > 1:
            sys.exit(f"ERROR: scale must be in (0, 1] (a fraction of the vanilla time); got {scale}")
        want = {name: round(vs * scale, 6) for name, (vs, _ve) in EXPECTED.items()}

    # Pre-flight: the input must look like the vanilla bundle we expect.
    before = read_times(in_path)
    missing = [n for n in EXPECTED if n not in before]
    if missing:
        sys.exit(f"ERROR: expected bag actors not present: {missing}. "
                 f"Refusing to patch an unexpected bundle.")

    env = UnityPy.load(in_path)
    changes = []
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        tree = obj.read_typetree()
        name = tree.get("m_Name") if isinstance(tree, dict) else None
        if not (isinstance(name, str) and name in EXPECTED):
            continue
        data = _bag_settings(tree)
        if data is None:
            sys.exit(f"ERROR: {name} has no LeafBagBehaviourSettings RefId; bundle shape changed.")
        orig_seal = float(data["sealingTime"])
        orig_empty = float(data.get("emptyingDuration", 0.0))
        exp_seal, exp_empty = EXPECTED[name]
        if not (_close(orig_seal, exp_seal) and _close(orig_empty, exp_empty)):
            sys.exit(f"ERROR: {name} is sealing={orig_seal}/emptying={orig_empty}, not the "
                     f"expected vanilla {exp_seal}/{exp_empty}. Revert any prior mod first "
                     f"(revert.ps1 / uninstall-all.ps1) before re-applying.")
        new_seal = want[name]
        ratio = new_seal / exp_seal
        data["sealingTime"] = new_seal
        new_empty = round(exp_empty * ratio, 6) if also_emptying else orig_empty
        if also_emptying:
            data["emptyingDuration"] = new_empty
        obj.save_typetree(tree)
        changes.append({"name": name, "seal_from": orig_seal, "seal_to": new_seal,
                        "empty_from": orig_empty, "empty_to": new_empty})

    if len(changes) != len(EXPECTED):
        sys.exit(f"ERROR: patched {len(changes)} of {len(EXPECTED)} expected bag actors.")

    os.makedirs(out_dir, exist_ok=True)
    env.save(pack="original", out_path=out_dir)

    produced = glob.glob(os.path.join(out_dir, "*.bundle"))
    if not produced:
        sys.exit("ERROR: UnityPy produced no .bundle in the output dir.")
    want_base = os.path.splitext(os.path.basename(in_path))[0] + ".bundle"
    out_path = os.path.join(out_dir, want_base)
    if not os.path.isfile(out_path):
        out_path = produced[0]

    # Post-write verification: re-open the OUTPUT and assert the new values.
    after = read_times(out_path)
    for c in changes:
        a = after.get(c["name"])
        if a is None or not _close(a[0], c["seal_to"]):
            sys.exit(f"ERROR: verify failed for {c['name']}: output has {a}, "
                     f"expected sealing {c['seal_to']}. Output NOT applied.")
        if also_emptying and not _close(a[1], c["empty_to"]):
            sys.exit(f"ERROR: verify failed for {c['name']} emptying: output has {a[1]}, "
                     f"expected {c['empty_to']}. Output NOT applied.")

    report = {
        "in": in_path,
        "out": out_path,
        "mode": "targets" if targets is not None else "scale",
        "scale": scale if targets is None else None,
        "alsoEmptying": also_emptying,
        "changes": changes,
        "verified": True,
        "input_size": os.path.getsize(in_path),
        "output_size": os.path.getsize(out_path),
    }
    print(json.dumps(report, indent=2))
    return out_path


def main():
    ap = argparse.ArgumentParser(description="Set the four Leaf it Alone bag tying times (multiplier or explicit seconds).")
    ap.add_argument("--in", dest="in_path", required=True, help="source actors .bundle (read-only)")
    ap.add_argument("--out-dir", dest="out_dir", help="dir to write the patched bundle into (required unless --verify-only)")
    ap.add_argument("--scale", type=float, default=0.5, help="fraction of the vanilla tying time 1.0/1.35/1.7/1.8 (default 0.5 = 2x faster); ignored if any of --small/--medium/--big/--giant is given")
    ap.add_argument("--small", type=float, help="explicit SmallLeafBag sealingTime seconds")
    ap.add_argument("--medium", type=float, help="explicit MediumLeafBag sealingTime seconds")
    ap.add_argument("--big", type=float, help="explicit BigLeafBag sealingTime seconds")
    ap.add_argument("--giant", type=float, help="explicit GiantLeafBag sealingTime seconds")
    ap.add_argument("--also-emptying", dest="also_emptying", action="store_true", help="also scale emptyingDuration by the same per-bag ratio")
    ap.add_argument("--verify-only", action="store_true", help="just print current tying times and exit")
    args = ap.parse_args()

    if args.verify_only:
        print(json.dumps(read_times(args.in_path), indent=2))
        return

    if not args.out_dir:
        ap.error("--out-dir is required unless --verify-only is set")

    explicit = [args.small, args.medium, args.big, args.giant]
    if any(v is not None for v in explicit):
        missing = [lbl for lbl, v in zip(("small", "medium", "big", "giant"), explicit) if v is None]
        if missing:
            ap.error("when giving explicit seconds you must give all of --small/--medium/--big/--giant "
                     "(missing: %s)" % ",".join(missing))
        targets = {
            "SmallLeafBag": args.small,
            "MediumLeafBag": args.medium,
            "BigLeafBag": args.big,
            "GiantLeafBag": args.giant,
        }
        patch(args.in_path, args.out_dir, targets=targets, also_emptying=args.also_emptying)
    else:
        patch(args.in_path, args.out_dir, scale=args.scale, also_emptying=args.also_emptying)


if __name__ == "__main__":
    main()
