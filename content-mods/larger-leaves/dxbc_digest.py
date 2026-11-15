#!/usr/bin/env python3
"""Reproduce Unity's baked-DXBC container digest (the 16 bytes at container+4).

Root cause of the vanished-leaves patch was established by probe_hash_gate.py:
the offset-4 field is a *validated* content digest, so any in-place bytecode
edit must recompute it. Brute-forcing md5/sha/blake over byte-ranges failed
because the algorithm is NOT a plain hash of any slice.

The real algorithm (from the DXBC container spec / Kalmarv dxcontainer
`checksum`): MD5's raw block-compression function run over container[20:],
with a bespoke final block:

    last[0:4]      = bit count of the message (len*8, 32-bit LE)
    last[4:4+rest] = the trailing message bytes
    last[4+rest]   = 0x80
    (zeros up to byte 60)
    last[60:64]    = (bits >> 2) | 1   (dword count, low bit set, 32-bit LE)

If rest >= 56 the trailing bytes get their own block first (rest..0x80),
then the all-but-length final block is compressed.

This module implements md5_compress in pure Python (hashlib cannot be fed raw
blocks) and verifies it against every pristine container in the bundle:
dxbc_digest(container) must equal container[4:20].
"""
import math
import struct
import sys

import lz4.block as lb
import UnityPy

MD5_INIT = [0x67452301, 0xEFCDAB89, 0x98BADCFE, 0x10325476]
_K = [int(2**32 * abs(math.sin(i + 1))) & 0xFFFFFFFF for i in range(64)]
_S = [7, 12, 17, 22, 5, 9, 14, 20, 4, 11, 16, 23, 6, 10, 15, 21]


def _rotl(x, c):
    return ((x << c) | (x >> (32 - c))) & 0xFFFFFFFF


def md5_compress(state, block):
    """Standard MD5 compression of one 64-byte block into the 4-word state."""
    assert len(block) == 64
    a, b, c, d = state
    m = struct.unpack("<16I", block)
    for i in range(64):
        if i < 16:
            f = (b & c) | (~b & d)
            g = i
        elif i < 32:
            f = (d & b) | (~d & c)
            g = (5 * i + 1) % 16
        elif i < 48:
            f = b ^ c ^ d
            g = (3 * i + 5) % 16
        else:
            f = c ^ (b | ~d)
            g = (7 * i) % 16
        f = (f + a + _K[i] + m[g]) & 0xFFFFFFFF
        a, d, c = d, c, b
        b = (b + _rotl(f, _S[(i // 16) * 4 + (i % 4)])) & 0xFFFFFFFF
    return [(state[0] + a) & 0xFFFFFFFF, (state[1] + b) & 0xFFFFFFFF,
            (state[2] + c) & 0xFFFFFFFF, (state[3] + d) & 0xFFFFFFFF]


def dxbc_digest(container):
    """Return the 16-byte digest the compiler stores at container[4:20]."""
    data = container[20:]
    bits = (len(data) * 8) & 0xFFFFFFFF
    state = list(MD5_INIT)
    nblocks = len(data) // 64
    for i in range(nblocks):
        state = md5_compress(state, data[i * 64:(i + 1) * 64])
    rest = data[nblocks * 64:]

    last = bytearray(64)
    if len(rest) < 56:
        last[4:4 + len(rest)] = rest
        last[4 + len(rest)] = 0x80
    else:
        first = bytearray(64)
        first[:len(rest)] = rest
        first[len(rest)] = 0x80
        state = md5_compress(state, bytes(first))
    last[0:4] = struct.pack("<I", bits)
    last[60:64] = struct.pack("<I", ((bits >> 2) | 1) & 0xFFFFFFFF)
    state = md5_compress(state, bytes(last))
    return struct.pack("<4I", *state)


def containers(dec):
    """Yield (offset, container_bytes) for every DXBC container, walking by size."""
    off = dec.find(b"DXBC")
    while 0 <= off < len(dec) - 28:
        size = struct.unpack_from("<I", dec, off + 24)[0]
        if size < 32 or off + size > len(dec):
            return
        yield off, bytes(dec[off:off + size])
        off = dec.find(b"DXBC", off + size)


def main():
    bundle = sys.argv[1] if len(sys.argv) > 1 else \
        r"..\backup\shadersize_20261115_172115\shaders.orig.bundle"
    env = UnityPy.load(bundle)
    total = ok = 0
    for o in env.objects:
        if o.type.name != "Shader":
            continue
        sh = o.read()
        try:
            dec = lb.decompress(bytes(sh.compressedBlob),
                                uncompressed_size=sum(sh.decompressedLengths[0]))
        except Exception:
            continue
        for off, blob in containers(dec):
            stored = blob[4:20]
            calc = dxbc_digest(blob)
            total += 1
            match = stored == calc
            ok += match
            if total <= 24 or not match:
                print(f"pid={o.path_id} @{off:<7} size={len(blob):<6} "
                      f"{'MATCH' if match else 'MISS '} stored={stored.hex()} calc={calc.hex()}")
    print(f"\n{ok}/{total} container digests reproduced.")
    return 0 if ok == total and total > 0 else 1


if __name__ == "__main__":
    sys.exit(main())
