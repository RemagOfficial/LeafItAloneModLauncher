"""Sample UnityPy patcher for a third-party mod source repo.

Mirrors what bag-capacity's patcher does but smaller: change ONE numeric field on
ONE MonoBehaviour in the actors bundle, save to a staging dir, and self-verify.

Usage:
    python patch_sample_bags.py --in <live.bundle> --out <staging_dir> --config <cfg.json>

Exit codes:
    0  OK - staged bundle in --out
    2  config or bundle path problem (nothing was written)
    3  verification failed (staged bundle was removed; live is untouched)
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

try:
    import UnityPy
except ImportError:
    print("UnityPy is not installed on this interpreter. Run: python -m pip install UnityPy", file=sys.stderr)
    sys.exit(2)


# The path_id / class-name of the MonoBehaviour carrying bagCapacities. Real mod
# authors discover these via read_typetree once and hard-code them (see the
# built-in bag-capacity patcher for the exact recipe).
BAG_SETTINGS_TYPE = "LeafBagSettings"
TARGET_FIELD = "bagCapacities"
TARGET_KEY = "small"


def load_config(path: Path) -> dict:
    with path.open("r", encoding="utf-8") as f:
        return json.load(f)


def patch(in_path: Path, out_dir: Path, capacity: int) -> Path:
    env = UnityPy.load(str(in_path))
    changed = 0
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        if tree.get("m_Script", {}).get("className") != BAG_SETTINGS_TYPE:
            continue
        caps = tree.get(TARGET_FIELD)
        if not isinstance(caps, dict) or TARGET_KEY not in caps:
            continue
        if caps[TARGET_KEY] == capacity:
            continue
        caps[TARGET_KEY] = capacity
        obj.save_typetree(tree)
        changed += 1
    if changed == 0:
        print("no matching MonoBehaviour found - is this the right bundle?", file=sys.stderr)
        sys.exit(3)

    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / in_path.name
    with out_path.open("wb") as f:
        f.write(env.file.save(pack="original"))
    return out_path


def verify(out_path: Path, expected: int) -> None:
    env = UnityPy.load(str(out_path))
    for obj in env.objects:
        if obj.type.name != "MonoBehaviour":
            continue
        try:
            tree = obj.read_typetree()
        except Exception:
            continue
        if tree.get("m_Script", {}).get("className") != BAG_SETTINGS_TYPE:
            continue
        caps = tree.get(TARGET_FIELD, {})
        if caps.get(TARGET_KEY) == expected:
            return
    print(f"post-patch verify failed: {TARGET_KEY} != {expected}", file=sys.stderr)
    sys.exit(3)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="in_path", required=True)
    ap.add_argument("--out", dest="out_dir", required=True)
    ap.add_argument("--config", dest="config", required=True)
    args = ap.parse_args()

    in_path = Path(args.in_path)
    out_dir = Path(args.out_dir)
    if not in_path.exists():
        print(f"input bundle missing: {in_path}", file=sys.stderr)
        return 2
    cfg = load_config(Path(args.config))
    capacity = int(cfg.get("smallCapacity", 8))

    staged = patch(in_path, out_dir, capacity)
    verify(staged, capacity)
    print(f"staged {staged.name} -> small capacity = {capacity}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
