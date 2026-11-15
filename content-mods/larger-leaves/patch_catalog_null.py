#!/usr/bin/env python3
r"""
patch_catalog_null.py - null ONE bundle's CRC in a Unity Addressables binary
catalog (catalog.bin), so the game stops refusing a repacked bundle.
UPDATE-ROBUST VERSION: it never hardcodes a CRC (those change with every game
update) - it anchors on the bundle's FILENAME, which the catalog stores as a
plain path string next to its record fields.

Background: Addressables loads each local bundle with
AssetBundle.LoadFromFileAsync(path, crc); the crc comes from this catalog. A
repacked bundle whose CRC no longer matches makes the game hang on a black
screen ("CRC Mismatch ... Will not load AssetBundle"). crc == 0 tells Unity to
skip the check, so we zero exactly those 4 bytes for the one bundle we patched.
The file length is unchanged, so every other offset stays valid.

How the record is located (all anchors come from the live install, so a game
update that rehashes filenames/CRCs cannot break this):
  * the bundle's filename (e.g. coreassets_assets_meta_<guid>.bundle) appears
    in the catalog twice: a key entry, then the long
    "...\StandaloneWindows64\<fname>" PATH entry - the one preceded by '\';
  * the record follows the path entry: [16 raw guid bytes][fields...][crc
    LE32][size LE32]; the guid TAIL (last 4-byte group, both byte orders)
    validates the window, and the crc is the 4 bytes BEFORE the live file
    size (an update that changes the crc rewrites the size beside it, so the
    pair stays adjacent while the values move);
  * an older [crc][guid tail][size] adjacency is kept as a secondary pattern.
Already-nullified records (4 zero bytes where the crc sits) are a successful
no-op, so co-mods and re-runs are safe.

Example:
  C:\\Python312\\python.exe patch_catalog_null.py --catalog <...aa\\catalog.bin> \
      --bundle-path <...\\coreassets_assets_meta_<guid>.bundle> [--dry-run]
"""
import argparse
import json
import os
import struct
import sys

WINDOW = 640  # record fields live within this many bytes after the name string


def guid_bytes_from_name(stem):
    """coreassets_assets_meta_3f08c135...b74e -> raw 16 bytes, LE per 4-byte group
    (matches how Unity writes the guid next to the record; verified in catalog.bin)."""
    tail = stem.rsplit("_", 1)[-1].lower()
    if len(tail) != 32:
        return None
    try:
        raw = bytes.fromhex(tail)
    except ValueError:
        return None
    out = bytearray(16)
    for g in range(4):  # bytes 0-3, 4-7, 8-11, 12-15 each byte-swapped
        for k in range(4):
            out[g * 4 + k] = raw[g * 4 + (3 - k)]
    return bytes(out)


def _all_offsets(hay, needle):
    """Every start offset of needle in hay (non-overlapping, byte search)."""
    offs = []
    i = hay.find(needle)
    while i != -1:
        offs.append(i)
        i = hay.find(needle, i + 1)
    return offs


def main():
    ap = argparse.ArgumentParser(description="Null one bundle's CRC in catalog.bin, anchored on its filename.")
    ap.add_argument("--catalog", required=True, help="path to catalog.bin")
    ap.add_argument("--bundle-path", required=True, dest="bundle_path",
                    help="the bundle file on disk (its name + size identify the catalog record)")
    ap.add_argument("--dry-run", action="store_true", help="locate + validate only, write nothing")
    args = ap.parse_args()

    for p in (args.catalog, args.bundle_path):
        if not os.path.isfile(p):
            sys.exit(f"ERROR: file not found: {p}")
    with open(args.catalog, "rb") as f:
        data = bytearray(f.read())
    fname = os.path.basename(args.bundle_path)
    size = os.path.getsize(args.bundle_path)
    if size > 0xFFFFFFFF:
        sys.exit("ERROR: bundle larger than 4 GB - cannot fit the size field anchor.")

    stem = fname[:-len(".bundle")] if fname.endswith(".bundle") else fname
    guid = guid_bytes_from_name(stem)
    if guid is None:
        sys.exit(f"ERROR: cannot parse a guid tail from filename '{fname}' - "
                 f"expected <key>_<32 hex>.bundle; refusing to guess.")

    # 1. find the filename string. It appears at least twice (a short key entry
    # and the long "...\StandaloneWindows64\<fname>" path entry). The RECORD
    # follows the PATH entry - identified by the preceding byte being '\'.
    fenc = fname.encode("utf-8")
    name_offs = []
    i = data.find(fenc)
    while i != -1:
        name_offs.append(i)
        i = data.find(fenc, i + 1)
    if len(name_offs) == 0:
        sys.exit(f"ERROR: bundle filename {fname} not in catalog at all. The install is "
                 f"half-updated (catalog references a different file). Finish the update or "
                 f"run Steam 'verify integrity of game files', then re-apply.")
    path_starts = [o for o in name_offs if o > 0 and data[o - 1:o] in (b"\\", b"/")]
    if not path_starts:
        path_starts = name_offs[-1:]   # no slash-prefixed hit: use the last occurrence
    guid_s = guid[12:16]
    guid_sw = bytes(reversed(guid_s))

    # 2. in each path window, the verified post-update layout is:
    #    "...<fname>.bundle" [16 raw guid bytes][fields...][crc LE32][size LE32]...
    #    so the PRIMARY anchor is the live file size (an update that rehashes the
    #    bundle also rewrites the size next to its crc), and the record is only
    #    trusted when this bundle's guid tail also sits in the same window (a
    #    stray size collision elsewhere in the catalog can never match it).
    #    The classic [size][guid tail] adjacency (older catalogs) is kept as a
    #    secondary pattern. Refuse on ambiguity.
    cands = []
    for ps in path_starts:
        region_start = ps + len(fname)
        region = bytes(data[region_start:region_start + WINDOW])
        tail_hits = _all_offsets(region, guid_sw) + _all_offsets(region, guid_s)
        if not tail_hits:
            continue  # this window is not this bundle's record
        # primary: [crc][size] with the live size, crc = the 4 bytes before it
        size_hits = _all_offsets(region, struct.pack("<I", size))
        for sp in size_hits:
            if sp < 4:
                continue
            crc_off = region_start + sp - 4
            cands.append((crc_off, bytes(data[crc_off:crc_off + 4]).hex(), "crc|size (guid-validated)"))
        # fallback: the live size is NOT in the record. This happens when a
        # co-mod applied earlier repacked the SAME bundle (its live size moved
        # off the vanilla size stored in the catalog) or a half-restored install
        # left the catalog describing a different file. The record is still this
        # guid-validated window, and the verified layout is
        # [16-byte guid][...fields...][crc LE32][size LE32] with the pair up to
        # ~120 bytes after the guid. Scan forward from the guid start for the
        # FIRST 4-byte field that is a plausible size (50%..200% of the live
        # size) and take the 4 bytes before it as the crc. A wrong guess lands
        # on garbage the co-mod's own apply would fail on, but the crc-off is
        # still inside this bundle's record, so nullifying it is harmless
        # (worst case: one other record's check is skipped too).
        if not size_hits:
            gstart = _all_offsets(region, guid) + _all_offsets(region, bytes(reversed(guid)))
            anchor = min(gstart) if gstart else min(tail_hits)
            for sp in range(anchor + 8, min(anchor + 120, len(region) - 4)):
                fs = struct.unpack("<I", region[sp:sp + 4])[0]
                if fs and size * 0.5 <= fs <= size * 2.0:
                    crc_off = region_start + sp - 4
                    cands.append((crc_off, bytes(data[crc_off:crc_off + 4]).hex(), "crc|size (plausible-size scan)"))
                    break
        # secondary (older layout): crc = the 4 bytes before the guid tail,
        # with the size adjacent on either side
        for tail_off in tail_hits:
            if tail_off < 4:
                continue
            crc_off = region_start + tail_off - 4
            size_after = struct.unpack("<I", bytes(data[crc_off + 4:crc_off + 8]))[0]
            size_before = struct.unpack("<I", bytes(data[crc_off - 4:crc_off]))[0]
            if size_after == size:
                cands.append((crc_off, bytes(data[crc_off:crc_off + 4]).hex(), "crc|tail|size"))
            elif size_before == size:
                cands.append((crc_off, bytes(data[crc_off:crc_off + 4]).hex(), "size|crc|tail"))
    # dedupe + drop the all-zero "already nullified" duplicates of a real hit
    uniq = {}
    for off, cb, layout in cands:
        uniq.setdefault(off, (cb, layout))
    live = [(off, cb, lay) for off, (cb, lay) in uniq.items()]
    if not live:
        sys.exit(f"ERROR: no catalog record for {fname} (size {size}) within {WINDOW} bytes "
                 f"of its path string - catalog layout changed or the file differs from the "
                 f"install the catalog describes. Refusing to guess.")
    nonzero = [(o, cb, lay) for o, cb, lay in live if cb != "00000000"]
    chosen = nonzero if nonzero else live
    if len(chosen) > 1:
        sys.exit(f"ERROR: {len(chosen)} crc candidates for {fname} at {chosen[0][0]}.. - "
                 f"ambiguous record. Refusing to guess.")
    off, crc_before, layout = chosen[0]
    already = (crc_before == "00000000")
    stored_size = struct.unpack("<I", bytes(data[off + 4:off + 8]))[0]

    report = {
        "catalog": args.catalog,
        "bundle": fname,
        "bundleSize": size,
        "pathStringOffset": off,
        "crcOffset": off,
        "layout": layout,
        "crcBefore": crc_before,
        "sizeAdjacent": stored_size,
        "action": None,
    }
    if already:
        report["action"] = "already-nullified (no-op)"
        print(json.dumps(report, indent=2))
        return

    if not args.dry_run:
        data[off:off + 4] = b"\x00\x00\x00\x00"
        tmp = args.catalog + ".safebase.tmp"
        with open(tmp, "wb") as f:
            f.write(bytes(data))
        os.replace(tmp, args.catalog)
        report["action"] = "nullified"
    else:
        report["action"] = "would-nullify"
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
