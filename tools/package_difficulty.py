"""Build deterministic, uncompressed VPK v2 difficulty packs (stdlib only)."""
from __future__ import annotations
import argparse
from pathlib import Path, PurePosixPath
import struct
import zlib

ROOT = Path(__file__).resolve().parents[1]


def make_vpk(files: dict[str, bytes]) -> bytes:
    groups: dict[str, dict[str, dict[str, bytes]]] = {}
    for name, data in sorted(files.items()):
        p = PurePosixPath(name)
        if p.is_absolute() or ".." in p.parts:
            raise ValueError("VPK paths must be relative")
        groups.setdefault(p.suffix[1:] or " ", {}).setdefault(
            str(p.parent) if str(p.parent) != "." else " ", {}
        )[p.stem] = data
    tree, payload = bytearray(), bytearray()
    for extension, paths in sorted(groups.items()):
        tree += extension.encode() + b"\0"
        for path, entries in sorted(paths.items()):
            tree += path.encode() + b"\0"
            for name, data in sorted(entries.items()):
                tree += name.encode() + b"\0"
                tree += struct.pack("<IHHIIH", zlib.crc32(data), 0, 0x7FFF,
                                    len(payload), len(data), 0xFFFF)
                payload += data
            tree += b"\0"
        tree += b"\0"
    tree += b"\0"
    return struct.pack("<7I", 0x55AA1234, 2, len(tree), len(payload), 0, 0, 0) + tree + payload


def difficulty_files(level: str) -> dict[str, bytes]:
    return {
        "botprofile.db": (ROOT / "overrides" / level / "botprofile.db").read_bytes(),
        "scripts/ai/rush/bt_default.kv3": (ROOT / "overrides/scripts/bt_default.kv3").read_bytes(),
        "scripts/ai/rush/bt_config.kv3": (ROOT / "overrides/scripts" / level / "bt_config.kv3").read_bytes(),
        "cfg/bot_human_active.cfg": (
            f"// Humanized bots: {level}; loaded from the selected difficulty VPK.\n"
            f"bot_human_difficulty {level.lower()}\n"
        ).encode(),
    }


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=ROOT / "dist/overrides")
    args = parser.parse_args()
    for level in ("Low", "Medium", "High"):
        target = args.output / level / "botprofile.vpk"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(make_vpk(difficulty_files(level)))
        print(target)
