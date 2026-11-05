#!/usr/bin/env python3
"""
patch_positions.py - the more-leaves v1.3 editor for the leaf-scatter position BANK.

Recon (MODLOG) proved leaves draw from a FIXED bank of scatter positions stored as a
binary TextAsset InitialLeaves_<World> inside coreassets_assets_leaves. Format (proven
by whole-file fit + a structure survey):
    [int32 count][count x 16-byte records]   record = float32 x, y, z, <4th float=0.0>
The 4th field is constant 0.0 (padding), and the records are ordered as CONTIGUOUS
PER-AREA BLOCKS: the first area's positions, then the next area's, ... matching the
scene/prefab initArgs[] order (the leading records are a tight same-height cluster). The
bank count equals the scene pool (Hometown 102400, Castle 140288, Sakura 260096), and the
spawner reads each area as bank[offset : offset + areaCount] with offset = the running sum
of the (scaled) per-area counts.

=> The extra positions MUST be inserted IN PLACE (each record expanded to N consecutive
records: the original + N-1 nearby copies), NOT appended at the tail. Appending keeps the
originals as the leading block but shifts every area's window into the NEXT area's
positions, so each area reads wrong-region leaves and only the LAST area gets the copies -
this was the non-monotonic 4x/6x/10x pickup bug. In-place expansion makes each area's
contiguous block grow exactly xN, staying aligned with the scaled initArgs offsets.

initArgs[].leavesCount (spawn budget) and the completion target can only draw DOWN from
this bank, never up - so MORE leaves requires MORE positions here.

Modes:
  dup-exact  each record repeated --scale-* times at the SAME spot (stacked).
  scatter    each record repeated, copies JITTERED in x/z only (keep y + 4th field), so
             the extra leaves are new nearby spots on the ground -> visibly denser. y is
             kept because leaf heights are per-area discrete levels (no terrain raycast).

One pass edits ALL requested worlds' banks in the SAME env, then writes once (the three
banks share one bundle). Deterministic via a fixed RNG seed so re-runs are byte-identical.

Safety, mirroring patch_leaves.py: only reads --in, writes into --out-dir, NEVER touches
the game install. After writing it RE-OPENS the output and asserts each patched bank's
count == old*scale, the whole file parses (4+count*16), every original record still sits at
its interleaved stride (out[i*scale] == orig[i]), and each scatter copy is within the
jitter radius of its own parent record - else it exits non-zero so the caller leaves the
game be.

Run with the pinned interpreter (UnityPy's lz4/brotli deps segfault on 3.14):
    C:\\Python312\\python.exe patch_positions.py --help
"""
import argparse
import glob
import json
import os
import random
import struct
import sys

import UnityPy

WORLDS = {
    "hometown": "InitialLeaves_Hometown",
    "castle": "InitialLeaves_Castle",
    "sakura": "InitialLeaves_Sakura",
}
REC = 16  # bytes per position record


def _find_banks(env):
    """Return {world: (obj, typetree_dict)} for every InitialLeaves_<World> TextAsset."""
    found = {}
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        try:
            d = obj.read_typetree()
        except Exception:
            continue
        name = d.get("m_Name", "")
        for world, wname in WORLDS.items():
            if name == wname:
                found[world] = (obj, d)
    return found


def _raw_script(tree):
    s = tree.get("m_Script", "")
    if isinstance(s, (bytes, bytearray)):
        return bytes(s)
    return str(s).encode("utf-8", "surrogateescape")


def parse_bank(raw):
    count = struct.unpack_from("<i", raw, 0)[0]
    body = raw[4:]
    if count < 0 or len(body) != count * REC:
        raise ValueError(f"bank layout mismatch: count={count} body={len(body)} "
                         f"(expected {count}*{REC}={count*REC})")
    return count, body


def _expand(body, count, scale, mode, radius, seed):
    """In-place expand every record to `scale` consecutive records. For each original the
    first emitted record is the original itself (so out[i*scale] == in[i]) and the rest are
    copies: scatter jitters x/z by a uniform offset in +-radius (keeping y + the 4th field);
    dup-exact repeats the record verbatim. Deterministic seed -> byte-identical re-runs.
    This preserves the per-area contiguous-block order (each block grows exactly xScale)."""
    rng = random.Random(seed)
    out = bytearray()
    for i in range(count):
        r = body[i * REC:(i + 1) * REC]
        out += r                                   # original at its stride position
        if mode == "scatter":
            x, y, z, w = struct.unpack("<4f", r)
            for _ in range(scale - 1):
                jx = x + rng.uniform(-radius, radius)
                jz = z + rng.uniform(-radius, radius)
                out += struct.pack("<4f", jx, y, jz, w)
        else:                                      # dup-exact
            for _ in range(scale - 1):
                out += r
    return bytes(out), count * scale


def build_bank(raw, scale, mode, radius, seed):
    count, body = parse_bank(raw)
    if scale < 1:
        raise ValueError(f"scale must be >=1 (got {scale})")
    if scale == 1:
        return raw, count, count, body             # scale 1 => leave untouched
    new_body, new_count = _expand(body, count, scale, mode, radius, seed)
    return struct.pack("<i", new_count) + new_body, count, new_count, body


def verify_expand(orig_raw, new_raw, scale, mode, radius):
    """Assert the interleaved layout on the OUTPUT: count == old*scale, each original sits
    at out[i*scale], and each copy is a valid derivation of its own parent. Returns
    (ok, message)."""
    oc, ob = parse_bank(orig_raw)
    nc, nb = parse_bank(new_raw)
    if nc != oc * scale:
        return False, f"count {nc} != old {oc} * scale {scale}"
    if len(nb) != nc * REC:
        return False, "output body length mismatch"
    for i in range(oc):
        o_rec = ob[i * REC:(i + 1) * REC]
        base = i * scale * REC
        if nb[base:base + REC] != o_rec:
            return False, f"original not at stride for record {i}"
        ox, oy, oz, ow = struct.unpack("<4f", o_rec)
        for j in range(1, scale):
            c = struct.unpack("<4f", nb[base + j * REC: base + (j + 1) * REC])
            if c[1] != oy or c[3] != ow:
                return False, f"copy {j} of record {i}: y/4th not preserved"
            if mode == "scatter":
                if abs(c[0] - ox) > radius + 1e-4 or abs(c[2] - oz) > radius + 1e-4:
                    return False, f"copy {j} of record {i}: x/z drifted beyond radius"
            elif c != (ox, oy, oz, ow):
                return False, f"dup copy {j} of record {i} != original"
    return True, "ok"


def survey(bundle_path):
    env = UnityPy.load(bundle_path)
    banks = _find_banks(env)
    out = {}
    for world, (obj, tree) in banks.items():
        raw = _raw_script(tree)
        count, _ = parse_bank(raw)
        out[world] = {"name": WORLDS[world], "count": count,
                      "bytes": len(raw), "path_id": str(obj.path_id)}
    return out


def patch(in_path, out_dir, scales, mode, radius, seed):
    if not os.path.isfile(in_path):
        sys.exit(f"ERROR: input bundle not found: {in_path}")
    for w, s in scales.items():
        if s < 1:
            sys.exit(f"ERROR: scale for {w} must be >=1 (got {s}).")

    env = UnityPy.load(in_path)
    banks = _find_banks(env)
    missing = [w for w, s in scales.items() if s > 1 and w not in banks]
    if missing:
        sys.exit(f"ERROR: banks not found in bundle for {missing}. Wrong bundle?")

    plan = []
    for world, (obj, tree) in banks.items():
        scale = scales.get(world, 1)
        orig = _raw_script(tree)                    # capture before mutating the tree
        new_raw, old_count, new_count, orig_body = build_bank(orig, scale, mode, radius, seed)
        if scale <= 1:
            plan.append({"world": world, "scale": scale, "old_count": old_count,
                         "new_count": new_count, "changed": False})
            continue
        # pre-save sanity: the in-place expansion must keep every original at its stride.
        ok, msg = verify_expand(orig, new_raw, scale, mode, radius)
        if not ok:
            sys.exit(f"ERROR: {world}: pre-save expand check failed: {msg}")
        tree["m_Script"] = new_raw.decode("utf-8", "surrogateescape")
        obj.save_typetree(tree)
        plan.append({"world": world, "scale": scale, "mode": mode, "radius": radius,
                     "old_count": old_count, "new_count": new_count, "changed": True,
                     "orig_body_len": len(orig_body)})

    os.makedirs(out_dir, exist_ok=True)
    env.save(pack="original", out_path=out_dir)
    produced = glob.glob(os.path.join(out_dir, "*.bundle"))
    if not produced:
        sys.exit("ERROR: UnityPy produced no .bundle in the output dir.")
    want_base = os.path.splitext(os.path.basename(in_path))[0] + ".bundle"
    out_path = os.path.join(out_dir, want_base)
    if not os.path.isfile(out_path):
        out_path = produced[0]

    # Post-write verification on the RE-OPENED OUTPUT: count == old*scale, originals at
    # their stride, and copies valid, for every changed world.
    got = survey(out_path)
    orig_banks = _find_banks(UnityPy.load(in_path))   # pristine file on disk
    out_banks = _find_banks(UnityPy.load(out_path))    # patched file on disk
    for p in plan:
        if not p["changed"]:
            continue
        w = p["world"]
        if w not in got or got[w]["count"] != p["new_count"]:
            sys.exit(f"ERROR: verify failed on {w}: count {got.get(w, {}).get('count')} "
                     f"!= expected {p['new_count']}.")
        o_raw = _raw_script(orig_banks[w][1])
        n_raw = _raw_script(out_banks[w][1])
        ok, msg = verify_expand(o_raw, n_raw, p["scale"], mode, radius)
        if not ok:
            sys.exit(f"ERROR: verify failed on {w}: {msg}")

    report = {"in": in_path, "out": out_path, "mode": mode, "seed": seed,
              "radius": radius, "plan": plan,
              "input_size": os.path.getsize(in_path),
              "output_size": os.path.getsize(out_path), "verified": True}
    print(json.dumps(report, indent=2))
    return out_path


def main():
    ap = argparse.ArgumentParser(
        description="In-place expand the InitialLeaves_<world> position bank(s) in "
                    "coreassets_assets_leaves, preserving the per-area block order "
                    "(multi-world single pass).")
    ap.add_argument("--in", dest="in_path", required=True, help="coreassets_assets_leaves .bundle (read-only)")
    ap.add_argument("--out-dir", dest="out_dir", help="dir to write the patched bundle (required unless --verify-only)")
    ap.add_argument("--scale-hometown", type=int, default=1, dest="sh", help="hometown bank expand factor (1 = leave alone)")
    ap.add_argument("--scale-castle", type=int, default=1, dest="sc", help="castle bank expand factor")
    ap.add_argument("--scale-sakura", type=int, default=1, dest="ss", help="sakura bank expand factor")
    ap.add_argument("--mode", default="scatter", choices=["scatter", "dup-exact"])
    ap.add_argument("--radius", type=float, default=0.6, help="scatter jitter radius in x/z metres (scatter mode)")
    ap.add_argument("--seed", type=int, default=1337, help="RNG seed for deterministic scatter")
    ap.add_argument("--verify-only", action="store_true", help="print the current bank survey and exit")
    args = ap.parse_args()

    if args.verify_only:
        print(json.dumps(survey(args.in_path), indent=2))
        return
    if not args.out_dir:
        ap.error("--out-dir is required unless --verify-only is set")

    scales = {"hometown": args.sh, "castle": args.sc, "sakura": args.ss}
    patch(args.in_path, args.out_dir, scales, args.mode, args.radius, args.seed)


if __name__ == "__main__":
    main()
