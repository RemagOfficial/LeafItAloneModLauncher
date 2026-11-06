#!/usr/bin/env python3
"""
patch_catalog.py - null a single bundle's CRC in a Unity Addressables
binary catalog (catalog.bin), so the game stops refusing a repacked bundle.

Background: Addressables loads each local bundle with
AssetBundle.LoadFromFileAsync(path, crc). The `crc` comes from the catalog's
per-bundle AssetBundleRequestOptions (CRC is stored little-endian, immediately
followed by the bundle size). If we change a bundle's bytes its computed CRC
no longer matches the catalog's stored value and the game logs
"CRC Mismatch ... Will not load AssetBundle" and hangs. Passing crc == 0 tells
Unity to skip the check entirely, so we zero just those 4 bytes for the one
bundle we patched. The file length is unchanged, so every other offset in the
catalog stays valid.

Safety guards (it refuses rather than guessing):
  * the crc anchor must be non-zero and found EXACTLY once in the catalog;
  * the 4 bytes right after it must decode to --expect-size (the VANILLA bundle
    size), which confirms we're on the bundle's options record and not a
    coincidental byte match.
Only then does it write zeros over the 4 crc bytes, atomically.

Co-existing mods (--allow-absent): several content mods patch the SAME actors
bundle (faster-bag-tying and sakura-petals-everywhere), so whichever runs first
already zeroed this record's CRC. With --allow-absent, if the live CRC anchor is
no longer present we confirm the record is *already* nullified - exactly one spot
where a 4-byte zero CRC is immediately followed by --expect-size (the vanilla
size, which the catalog keeps) - and succeed as a no-op instead of erroring. Pass
--expect-size as the constant VANILLA bundle size (not the current patched file
size) so this works regardless of which mod ran first.

Example:
  C:\\Python312\\python.exe patch_catalog.py --catalog <...aa\\catalog.bin> \
      --crc 88295665 --expect-size 27021 --allow-absent
"""
import argparse
import json
import os
import struct
import sys


def find_all(data, value_bytes):
    offs = []
    i = data.find(value_bytes)
    while i != -1:
        offs.append(i)
        i = data.find(value_bytes, i + 1)
    return offs


def main():
    ap = argparse.ArgumentParser(description="Null one bundle's CRC in catalog.bin.")
    ap.add_argument("--catalog", required=True, help="path to catalog.bin")
    ap.add_argument("--crc", required=True, help="the bundle's CURRENT catalog CRC, 8 hex chars (e.g. 88295665)")
    ap.add_argument("--expect-size", type=int, required=True, dest="expect_size",
                    help="VANILLA bundle file size; confirms the record (crc is stored just before size)")
    ap.add_argument("--allow-absent", action="store_true", dest="allow_absent",
                    help="treat an already-nullified record (zero CRC + expect-size) as success, for mods that share a bundle")
    ap.add_argument("--dry-run", action="store_true", help="locate + validate only, write nothing")
    args = ap.parse_args()

    if not os.path.isfile(args.catalog):
        sys.exit(f"ERROR: catalog not found: {args.catalog}")
    with open(args.catalog, "rb") as f:
        data = bytearray(f.read())

    try:
        crc = int(args.crc, 16)
    except ValueError:
        sys.exit(f"ERROR: --crc is not hex: {args.crc}")
    if crc == 0:
        sys.exit("ERROR: refusing crc anchor of 0 (nothing to nullify / bad input)")

    pat = struct.pack("<I", crc)
    offs = find_all(data, pat)

    # Case B: the live CRC anchor is gone. Either it was never here (wrong game
    # version) or another mod already nullified this exact record.
    if len(offs) == 0:
        if not args.allow_absent:
            sys.exit(f"ERROR: crc {args.crc} not present in catalog. Wrong game version, "
                     f"or the catalog was already patched. Refusing to touch catalog.bin.")
        zeroed = struct.pack("<II", 0, args.expect_size)
        zoffs = find_all(data, zeroed)
        if len(zoffs) == 1:
            report = {
                "catalog": args.catalog,
                "crcOffset": zoffs[0],
                "crcBefore": "00000000",
                "sizeConfirmed": args.expect_size,
                "crcAfter": "00000000",
                "action": "already-nullified",
            }
            print(json.dumps(report, indent=2))
            return
        sys.exit(f"ERROR: crc {args.crc} absent AND no unique already-nullified record "
                 f"(zero-CRC + size {args.expect_size}) found ({len(zoffs)} matches). "
                 f"Cannot confirm the record; refusing to touch catalog.bin.")

    if len(offs) > 1:
        sys.exit(f"ERROR: crc {args.crc} occurs {len(offs)} times, not unique. "
                 f"Refusing to guess which record to patch.")
    off = offs[0]

    if off + 8 > len(data):
        sys.exit("ERROR: crc found at end of file with no following size field.")
    stored_size = struct.unpack("<I", bytes(data[off + 4:off + 8]))[0]
    if stored_size != args.expect_size:
        sys.exit(f"ERROR: record check failed - 4 bytes after crc decode to size {stored_size}, "
                 f"expected the vanilla bundle size {args.expect_size}. This is not the bundle's "
                 f"options record; refusing to edit catalog.bin.")

    report = {
        "catalog": args.catalog,
        "crcOffset": off,
        "crcBefore": format(crc, "08x"),
        "sizeConfirmed": stored_size,
        "crcAfter": "00000000",
        "action": "would-nullify" if args.dry_run else "nullified",
    }

    if args.dry_run:
        print(json.dumps(report, indent=2))
        return

    data[off:off + 4] = b"\x00\x00\x00\x00"
    tmp = args.catalog + ".safebase.tmp"
    with open(tmp, "wb") as f:
        f.write(bytes(data))
    os.replace(tmp, args.catalog)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
