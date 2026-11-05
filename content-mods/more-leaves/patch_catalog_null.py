#!/usr/bin/env python3
"""
patch_catalog_null.py - null ONE bundle's CRC in a Unity Addressables binary
catalog (catalog.bin), so the game stops refusing a repacked bundle.

Same mechanism as the bag-capacity mod's patch_catalog.py (see
content-mods/bag-capacity/patch_catalog.py for the full Addressables background:
a local bundle is loaded with LoadFromFileAsync(path, crc) where crc comes from
this catalog, and a repacked bundle whose CRC no longer matches makes the game
hang on "CRC Mismatch"). The difference is only how the record is LOCATED:

  * bag-capacity anchors on the bundle's KNOWN crc (a stable hardcoded value) and
    confirms it with the adjacent size;
  * more-leaves edits THREE different gameplay scene bundles and we do not hardcode
    their CRCs (they change when the game updates), so we anchor on each bundle's
    CURRENT on-disk SIZE instead and nullify the 4 bytes BEFORE it.

In an AssetBundleRequestOptions record the layout is [crc u32 LE][size u32 LE],
so the 4 bytes preceding a size field are that bundle's CRC.

Safety guards (it refuses rather than guessing):
  * the size must be found EXACTLY once in the catalog (a scene bundle's byte
    size is unique enough; if not, we do not guess which record it is);
  * the 4 bytes before it must be a NON-ZERO crc (a record already nullified, or
    a coincidental byte match, is rejected - re-running apply reverts first).
Only then does it write zeros over those 4 crc bytes, atomically (temp + os.replace).

Example:
  C:\\Python312\\python.exe patch_catalog_null.py --catalog <...aa\\catalog.bin> \
      --bundle-path <...\\scenes_scenes_castle_....bundle>
"""
import argparse
import json
import os
import struct
import sys


def find_all(data, value_bytes):
    offs, i = [], data.find(value_bytes)
    while i != -1:
        offs.append(i)
        i = data.find(value_bytes, i + 1)
    return offs


def main():
    ap = argparse.ArgumentParser(description="Null one bundle's CRC in catalog.bin, anchoring on its size.")
    ap.add_argument("--catalog", required=True, help="path to catalog.bin")
    grp = ap.add_mutually_exclusive_group(required=True)
    grp.add_argument("--bundle-path", dest="bundle_path", help="the bundle on disk; its file size locates the record")
    grp.add_argument("--expect-size", type=int, dest="expect_size", help="explicit vanilla bundle size (bytes)")
    ap.add_argument("--dry-run", action="store_true", help="locate + validate only, write nothing")
    args = ap.parse_args()

    if not os.path.isfile(args.catalog):
        sys.exit(f"ERROR: catalog not found: {args.catalog}")

    if args.bundle_path:
        if not os.path.isfile(args.bundle_path):
            sys.exit(f"ERROR: bundle not found: {args.bundle_path}")
        size = os.path.getsize(args.bundle_path)
    else:
        size = args.expect_size
    if size <= 0:
        sys.exit(f"ERROR: non-positive bundle size {size}")

    with open(args.catalog, "rb") as f:
        data = bytearray(f.read())

    szb = struct.pack("<I", size)
    offs = find_all(data, szb)
    if len(offs) == 0:
        sys.exit(f"ERROR: size {size} not present in catalog. Wrong game version, or the "
                 f"bundle/catalog changed since recon. Refusing to touch catalog.bin.")
    if len(offs) > 1:
        sys.exit(f"ERROR: size {size} occurs {len(offs)} times, not unique. "
                 f"Refusing to guess which record to patch.")
    off = offs[0]
    if off < 4:
        sys.exit("ERROR: size found at the very start of the catalog with no preceding crc field.")

    crc = bytes(data[off - 4:off])
    if crc == b"\x00\x00\x00\x00":
        sys.exit(f"ERROR: the 4 bytes before size {size} are already 0 - this record is already "
                 f"nullified (re-apply should revert to a pristine catalog first).")

    report = {
        "catalog": args.catalog,
        "bundleSize": size,
        "crcOffset": off - 4,
        "crcBefore": crc.hex(),
        "crcAfter": "00000000",
        "action": "would-nullify" if args.dry_run else "nullified",
    }

    if args.dry_run:
        print(json.dumps(report, indent=2))
        return

    data[off - 4:off] = b"\x00\x00\x00\x00"
    tmp = args.catalog + ".safebase.tmp"
    with open(tmp, "wb") as f:
        f.write(bytes(data))
    os.replace(tmp, args.catalog)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
