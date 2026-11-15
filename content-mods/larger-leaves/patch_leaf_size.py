#!/usr/bin/env python3
"""patch_leaf_size.py - make every drawn leaf visually larger.

The leaf sprite size is NOT data-driven: the leaf RENDER shader (baked Shader
in shaders_assets_all_*.bundle) draws each leaf by reading a 76-byte record out
of the `leaves` StructuredBuffer and normalising the mesh-quad corner to a
FIXED length, `div r0.w, l(0.141420), r0.w` (0.141420 = 0.1*sqrt(2), i.e. the
leaf radius in world units). Because the corner is normalised, scaling the mesh
verts does nothing, and `leafWidths` only drives pile physics - both already
proven in-game. So the one true visual lever is that baked DXBC immediate.

This patcher decompresses the Shader's LZ4HC sub-program blob, replaces every
occurrence of the size constant with constant*K (a fixed-width 4-byte swap, so
instruction offsets and the bytecode length are unchanged), recompresses and
re-saves the bundle. The constant appears once per vertex-program pass, nowhere
else in the leaf shader, so the edit is surgical.

Usage:
  python patch_leaf_size.py --in <bundle> --out-dir <dir> [--size 2.0]
  python patch_leaf_size.py --in <bundle> --verify-only [--size 2.0]
"""
import argparse
import math
import os
import struct
import sys

import UnityPy
import lz4.block as lb

from dxbc_digest import dxbc_digest

# baked leaf render shader in shaders_assets_all_*.bundle (path_id from v1.1.7
# recon; auto-detected by structure if a game update moves it)
LEAF_SHADER_PID = -2667211721113983401
BASE = struct.unpack("<f", bytes.fromhex("68d0103e"))[0]  # 0.14142000675201416


def recompute_digests(dec):
    """Rewrite every DXBC container's digest (bytes 4..20) in the decompressed
    shader blob in place. The digest is MD5's block function over container[20:],
    so it does NOT depend on the stale value already stored at [4:20]. Walking by
    the size field at +24 means an in-place 4-byte float swap (no length change)
    keeps every container boundary valid, so each digest is recomputed exactly.

    This is the step that was missing: the DXBC hash at container+4 is validated
    when the container is parsed, so editing the bytecode float without fixing it
    makes D3DDisassemble fail (E_FAIL) and Unity's D3D backend reject the vertex
    shader (E_INVALIDARG) -> the leaf carpet is simply not drawn. Returns the
    number of containers re-hashed."""
    off = dec.find(b"DXBC")
    n = 0
    while 0 <= off < len(dec) - 28:
        size = struct.unpack_from("<I", dec, off + 24)[0]
        if size < 32 or off + size > len(dec):
            break
        dec[off + 4:off + 20] = dxbc_digest(bytes(dec[off:off + size]))
        n += 1
        off = dec.find(b"DXBC", off + size)
    return n


def f32(v):
    return struct.pack("<f", v)


def blob_of(sh):
    comp = bytes(sh.compressedBlob)
    dlen = sum(sh.decompressedLengths[0])
    return lb.decompress(comp, uncompressed_size=dlen), comp, dlen


def safe_blob(sh):
    try:
        return blob_of(sh)[0]
    except Exception:
        return None


def find_leaf_shader(env, anchor):
    """Return (obj, shader, decompressed_blob) for the leaf render shader.
    Prefer the known pid; otherwise pick the first Shader blob that contains
    the anchor float. Non-leaf shaders may hold blobs our LZ4 settings cannot
    expand, so each probe is guarded."""
    shaders = [o for o in env.objects if o.type.name == "Shader"]
    # fast path: known pid -> trust it, return whatever its blob decodes to
    for o in shaders:
        if o.path_id == LEAF_SHADER_PID:
            sh = o.read()
            return o, sh, (safe_blob(sh) or b"")
    # robust path: any Shader whose blob carries the anchor
    for o in shaders:
        sh = o.read()
        dec = safe_blob(sh)
        if dec is not None and dec.count(anchor) >= 1:
            return o, sh, dec
    return None, None, None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="bundle", required=True)
    ap.add_argument("--out-dir", dest="outdir")
    ap.add_argument("--size", type=float, default=2.0,
                    help="visual size multiplier (1.0 = no-op/refuse)")
    ap.add_argument("--base", type=float, default=BASE,
                    help="the baked leaf-radius constant to look for")
    ap.add_argument("--verify-only", action="store_true")
    args = ap.parse_args()

    if args.size <= 0:
        print("ERROR: --size must be > 0", file=sys.stderr)
        return 2

    anchor = f32(args.base)
    target = f32(args.base * args.size)

    env = UnityPy.load(args.bundle)
    obj, sh, dec = find_leaf_shader(env, anchor)
    if obj is None:
        # maybe already scaled: look for the target bytes instead
        obj, sh, dec = find_leaf_shader(env, target)
        if obj is not None and args.verify_only:
            n = dec.count(target)
            print(f"VERIFY OK: {sh.m_Name!r} leaf shader already at x{args.size} "
                  f"({n} passes -> {args.base*args.size:.6f})")
            return 0
        print("ERROR: no leaf render shader with the size constant found; input "
              "may already be patched or this is the wrong bundle", file=sys.stderr)
        return 3

    dec_arr = bytearray(dec)
    a_count = dec_arr.count(anchor)
    t_count = dec_arr.count(target)

    if args.verify_only:
        same = abs(args.size - 1.0) < 1e-9
        print(f"VERIFY: shader={sh.m_Name!r} pid={obj.path_id} "
              f"anchor(x1)={a_count} target(x{args.size})={t_count} "
              f"passes={sh.stageCounts}")
        if same:
            # anchor == target: pristine means the constant is simply present
            print("OK - pristine vanilla (x1)" if a_count >= 1 else "MISMATCH", file=sys.stderr if a_count < 1 else sys.stdout)
            return 0 if a_count >= 1 else 4
        if t_count >= 1 and a_count == 0:
            print(f"OK - leaf size is x{args.size}")
            return 0
        print("MISMATCH - not the expected state", file=sys.stderr)
        return 4

    if abs(args.size - 1.0) < 1e-9:
        print("ERROR: --size 1.0 is a no-op; refusing to repack", file=sys.stderr)
        return 2
    if a_count == 0:
        print("ERROR: input is NOT pristine (0 anchor matches) - refusing to "
              "compound the scale", file=sys.stderr)
        return 5

    # fixed-width in-place swap; count must stay identical
    pos = 0
    replaced = 0
    while True:
        i = dec_arr.find(anchor, pos)
        if i < 0:
            break
        dec_arr[i:i + 4] = target
        pos = i + 4
        replaced += 1
    assert replaced == a_count, f"replaced {replaced} != count {a_count}"
    if dec_arr.count(anchor) != 0:
        print("ERROR: anchor bytes still present after patch", file=sys.stderr)
        return 6

    # CRITICAL: the bytecode edit invalidated every DXBC container digest; fix
    # them all, or Unity rejects the vertex shader and the leaves disappear.
    nhash = recompute_digests(dec_arr)

    newdec = bytes(dec_arr)
    rec = lb.compress(newdec, mode="high_compression", compression=9,
                      store_size=False)
    # sanity: our newdec must decompress back exactly
    if lb.decompress(rec, uncompressed_size=len(newdec)) != newdec:
        print("ERROR: recompressed blob does not round-trip", file=sys.stderr)
        return 7

    sh.compressedBlob = list(rec)
    sh.compressedLengths = [[len(rec)]]
    sh.save()

    if not args.outdir:
        print("ERROR: --out-dir required (not verify-only)", file=sys.stderr)
        return 2
    os.makedirs(args.outdir, exist_ok=True)
    env.save(pack="original", out_path=args.outdir)

    out = os.path.join(args.outdir, os.path.basename(args.bundle))
    print(f"PATCHED: {sh.m_Name!r}  {replaced} passes  {args.base:.6f} -> "
          f"{args.base*args.size:.6f} (x{args.size})  in {len(rec)}B blob -> {out}")

    # re-open the written bundle and prove the floats landed
    env2 = UnityPy.load(out)
    o2, s2, d2 = None, None, None
    for o in env2.objects:
        if o.type.name == "Shader" and o.path_id == obj.path_id:
            o2 = o
            s2 = o.read()
            d2, _, _ = blob_of(s2)
            break
    if d2 is None:
        print("ERROR: could not re-read the patched shader from output", file=sys.stderr)
        return 8
    got_t = d2.count(target)
    got_a = d2.count(anchor)
    print(f"RECHECK: target(x{args.size})={got_t} anchor(x1)={got_a}")
    if got_t == replaced and got_a == 0:
        print("OK - output verified")
        return 0
    print("ERROR: output recheck failed", file=sys.stderr)
    return 9


if __name__ == "__main__":
    sys.exit(main())
