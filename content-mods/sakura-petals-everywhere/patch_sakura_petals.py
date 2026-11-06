#!/usr/bin/env python3
"""
patch_sakura_petals.py - the sakura-petals-everywhere content mod's editor.

In Leaf it Alone the sakura map's "petals" are NOT particles and NOT the
`petalsort` minigame - they are the normal leaves with a different MATERIAL.
Each world has a scene-definition asset (a MonoBehaviour named Hometown /
Castle / Sakura) in the coreassets SCENES bundle whose [SerializeReference]
`LeafSettingsSceneProperty` (Eternity.Application.Leaves) carries:
    computeShader -> LeavesComputeShader   (shared, pid -177024365686256552)
    mesh          -> Plane                 (shared, pid 8366179956047951131)
    material      -> Material "Leaves"       (pid -4751811775384957180)  Hometown/Castle
                    Material "CherryLeaves" (pid  4121393598532695707)  Sakura
All three live in visuals_assets_leaves (PPtr m_FileID 5). The materials share
one shader and differ only by the albedo+normal texture pair, so swapping the
material reference is exactly what turns a world's leaves into petals.

This mod repoints the chosen worlds' `material` PPtr at CherryLeaves. Leaf
counts, scatter positions and bounds are untouched (they live per-area in the
InitialLeaves banks), and the leaves stay fully pickable. Reads TypeTrees
(preserved despite IL2CPP metadata encryption), writes
them back, repacks into --out-dir, then RE-OPENS the output and verifies before
exiting 0. A non-zero exit means nothing was written.

History: v1 repointed the LeafArea ACTORS' `prefabAssetReference.m_AssetGUID`
in the coreassets ACTORS bundle instead. That applied cleanly but changed
nothing visible - the GUID is not the appearance lever. v2 patches the scenes
asset, proven by a whole-corpus reference scan: only these three MonoBehaviours
reference the Leaves/CherryLeaves materials.

This script only ever reads the --in bundle and writes into --out-dir. It does
NOT touch the game install; apply.ps1 handles Backup-Original + the atomic
replace + Register-Mod via the safe-base SDK.

Run with the pinned interpreter (UnityPy's native lz4/brotli deps segfault on
Python 3.14):  C:\\Python312\\python.exe patch_sakura_petals.py --help
"""
import argparse
import glob
import json
import os
import sys

import UnityPy

LEAVES_MAT = -4751811775384957180     # Material "Leaves"       (normal autumn leaves)
CHERRY_MAT = 4121393598532695707      # Material "CherryLeaves" (the sakura petal look)

# world asset m_Name -> vanilla leaf material path_id. Sakura's own entry is
# the cherry anchor we use to sanity-check we're in the right bundle.
WORLDS = {
    "Hometown": LEAVES_MAT,
    "Castle":   LEAVES_MAT,
    "Sakura":   CHERRY_MAT,
}
CONVERTIBLE = ("Hometown", "Castle")


def _leaf_settings(tree):
    """Return the LeafSettingsSceneProperty RefId `data` dict from a world scene
    asset's typetree, or None."""
    for r in tree.get("references", {}).get("RefIds", []):
        if r.get("type", {}).get("class") == "LeafSettingsSceneProperty":
            return r.get("data")
    return None


def _leaf_material(tree):
    """Return the `material` PPtr dict from a world scene asset's
    LeafSettingsSceneProperty RefId, or None."""
    data = _leaf_settings(tree)
    return data.get("material") if data else None


def read_mats(bundle_path):
    """Return {m_Name: {"material": path_id, "widths": [16 floats]}} for the
    three world scene assets."""
    env = UnityPy.load(bundle_path)
    found = {}
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        tree = obj.read_typetree()
        if not isinstance(tree, dict):
            continue
        name = tree.get("m_Name")
        if isinstance(name, str) and name in WORLDS:
            data = _leaf_settings(tree)
            if data is not None and data.get("material") is not None:
                widths = [float(w) for w in data.get("leafWidths", [])]
                found[name] = {"material": int(data["material"]["m_PathID"]),
                               "widths": widths}
    return found


def patch(in_path, out_dir, worlds):
    """Point the given worlds' leaf material at CherryLeaves.

    The input bundle must still hold vanilla material references (revert first
    otherwise), so this always patches from a known baseline and never
    double-applies.
    """
    if not os.path.isfile(in_path):
        sys.exit(f"ERROR: input bundle not found: {in_path}")
    worlds = [w for w in worlds if w in CONVERTIBLE]
    if not worlds:
        sys.exit(f"ERROR: nothing to do - pass at least one of {list(CONVERTIBLE)}.")
    os.makedirs(out_dir, exist_ok=True)

    # Pre-flight: all three world assets exist with the vanilla materials we
    # expect (sakura already cherry = our anchor; targeted worlds still leaves).
    before = read_mats(in_path)
    missing = [n for n in WORLDS if n not in before]
    if missing:
        sys.exit(f"ERROR: expected world scene assets not present: {missing}. "
                 f"Refusing to patch an unexpected bundle.")
    if before["Sakura"]["material"] != CHERRY_MAT:
        sys.exit(f"ERROR: Sakura's leaf material is {before['Sakura']['material']}, not the "
                 f"expected CherryLeaves {CHERRY_MAT}. Wrong bundle or a prior "
                 f"mod changed it; refusing.")
    for w in worlds:
        cur = before[w]["material"]
        if cur == CHERRY_MAT:
            sys.exit(f"ERROR: {w} already uses the CherryLeaves material. Revert "
                     f"any prior mod first (revert.ps1 / uninstall-all.ps1) before "
                     f"re-applying - this patcher only converts from vanilla leaves.")
        if cur != LEAVES_MAT:
            sys.exit(f"ERROR: {w} has unexpected leaf material {cur}, not the "
                     f"vanilla Leaves {LEAVES_MAT}. Wrong bundle, game update, or "
                     f"another mod in the way; refusing to patch.")

    env = UnityPy.load(in_path)
    changes = []
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        tree = obj.read_typetree()
        if not isinstance(tree, dict):
            continue
        name = tree.get("m_Name")
        if name not in WORLDS:
            continue
        data = _leaf_settings(tree)
        if data is None or data.get("material") is None:
            sys.exit(f"ERROR: {name} has no LeafSettingsSceneProperty RefId; "
                     f"bundle shape changed.")
        change = {"name": name}
        if name in worlds:
            mat = data["material"]
            change["mat_from"] = int(mat["m_PathID"])
            mat["m_PathID"] = CHERRY_MAT   # same bundle (m_FileID 5), only the object swaps
            change["mat_to"] = CHERRY_MAT
        if len(change) == 1:               # not a targeted world this run
            continue
        obj.save_typetree(tree)
        changes.append(change)

    n_mat = sum(1 for c in changes if "mat_to" in c)
    if n_mat != len(worlds):
        sys.exit(f"ERROR: repointed {n_mat} of {len(worlds)} requested worlds.")

    env.save(pack="original", out_path=out_dir)

    produced = glob.glob(os.path.join(out_dir, "*.bundle"))
    if not produced:
        sys.exit("ERROR: UnityPy produced no .bundle in the output dir.")
    want_base = os.path.splitext(os.path.basename(in_path))[0] + ".bundle"
    out_path = os.path.join(out_dir, want_base)
    if not os.path.isfile(out_path):
        out_path = produced[0]

    # Post-write verification: re-open the OUTPUT and assert materials.
    after = read_mats(out_path)
    for c in changes:
        wname = c["name"]
        if "mat_to" in c and after[wname]["material"] != CHERRY_MAT:
            sys.exit(f"ERROR: verify failed for {wname}: output material "
                     f"{after[wname]['material']}, expected {CHERRY_MAT}. Output NOT applied.")
    for w in CONVERTIBLE:
        if w not in worlds and after[w]["material"] != LEAVES_MAT:
            sys.exit(f"ERROR: verify failed - unselected world {w} changed to "
                     f"{after[w]['material']}. Output NOT trustworthy.")
    if after["Sakura"]["material"] != CHERRY_MAT:
        sys.exit(f"ERROR: verify failed - Sakura material moved. Output NOT trustworthy.")

    report = {
        "in": in_path,
        "out": out_path,
        "targetMaterial": CHERRY_MAT,
        "changes": changes,
        "unchanged": {w: LEAVES_MAT for w in CONVERTIBLE if w not in worlds},
        "verified": True,
        "input_size": os.path.getsize(in_path),
        "output_size": os.path.getsize(out_path),
    }
    print(json.dumps(report, indent=2))
    return out_path


def main():
    ap = argparse.ArgumentParser(
        description="Point chosen worlds' leaf material at the sakura CherryLeaves material.")
    ap.add_argument("--in", dest="in_path", required=True, help="source scenes-definition .bundle (read-only)")
    ap.add_argument("--out-dir", dest="out_dir", help="dir to write the patched bundle into (required unless --verify-only)")
    ap.add_argument("--hometown", action="store_true", help="convert Hometown's leaves to petals")
    ap.add_argument("--castle", action="store_true", help="convert the Castle's leaves to petals")
    ap.add_argument("--all", action="store_true", help="convert both worlds (default when neither flag is given)")
    ap.add_argument("--verify-only", action="store_true", help="just print current leaf materials + widths and exit")
    args = ap.parse_args()

    if args.verify_only:
        print(json.dumps(read_mats(args.in_path), indent=2))
        return

    if not args.out_dir:
        ap.error("--out-dir is required unless --verify-only is set")

    worlds = []
    if args.hometown:
        worlds.append("Hometown")
    if args.castle:
        worlds.append("Castle")
    if args.all or not worlds:
        worlds = list(CONVERTIBLE)

    patch(args.in_path, args.out_dir, worlds)


if __name__ == "__main__":
    main()
