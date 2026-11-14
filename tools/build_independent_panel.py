"""Reproducible UI overlay on the pinned, published Panel 1.4.8 executable.

Requires brotli and pefile. No Rust instructions, game plugins, settings or
licences are changed. Refuse unknown binaries rather than patching by guess.
The unpublished native backend still contains an unused release-query handler;
the frontend has no callers and contains no upstream release endpoints.
"""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import re
import struct

import brotli
import pefile

SOURCE_SHA256 = "dd29fe27b4b662d0341b14677427dbd978195620b8f1a7cb4372162c5136a087"
BUNDLE_SHA256 = "628b9836fc47b2d5ce2ce7d0cef0609be5f9c4d0eec564eba48881dddbf19cba"
OFFSET, CAPACITY, LENGTH_OFFSET = 0x55A412, 89133, 5754440 + 8
PROJECT = "https://github.com/HuangJZh/CS2-Bot-Improver"
ROOT = Path(__file__).resolve().parents[1]


def transform(source):
    # Remove the complete checker lifecycle and clickable update status.
    start = source.index("function Kp(")
    end = source.index("function ", start + len("function Kp("))
    source = source[:start] + (ROOT / "Panel/packaged/titlebar.js").read_text(encoding="utf-8") + source[end:]
    # Replace the encoded upstream/mirror release configuration, retain credits.
    encoded = re.search(r'const wc="([^"]+)",wp="([^"]+)"', source)
    if not encoded:
        raise ValueError("Pinned project configuration not found")
    key = encoded[1].encode()
    raw = base64.b64decode(encoded[2])
    config = json.loads(bytes(v ^ key[i % len(key)] for i, v in enumerate(raw)).decode())
    config = {"u": PROJECT, "d": ["HuangJZh", *config["d"]]}
    start, end = source.index("const wc="), source.index("function Dp(")
    source = source[:start] + "const xn=" + json.dumps(config, ensure_ascii=False, separators=(",", ":")) + ",Sc=xn.u,Ap=xn.d;" + source[end:]
    assert "latest_release_tag" not in source
    assert "releases/latest" not in source
    assert "api.gitcode.com" not in source
    assert "titlebar__ver--" not in source
    assert "v1.4.8" not in source
    return source


def build(original, output):
    original, output = Path(original), Path(output)
    if original.resolve() == output.resolve():
        raise ValueError("Output must differ from input")
    before = original.read_bytes()
    if hashlib.sha256(before).hexdigest() != SOURCE_SHA256:
        raise ValueError("Unknown Panel executable; expected pinned upstream 1.4.8")
    raw = brotli.decompress(before[OFFSET:OFFSET + CAPACITY])
    if hashlib.sha256(raw).hexdigest() != BUNDLE_SHA256:
        raise ValueError("Unexpected bundled frontend")
    source = transform(raw.decode("utf-8"))
    packed = brotli.compress(source.encode("utf-8"), quality=11)
    if len(packed) > CAPACITY:
        raise ValueError("New UI exceeds reserved asset capacity")
    data = bytearray(before)
    data[OFFSET:OFFSET + CAPACITY] = packed + bytes(CAPACITY - len(packed))
    struct.pack_into("<Q", data, LENGTH_OFFSET, len(packed))
    # Windows Details version. Same-length strings, no resource relocation.
    pe = pefile.PE(data=bytes(data))
    for group in pe.FileInfo:
        for block in group:
            for table in getattr(block, "StringTable", []):
                for key in (b"FileVersion", b"ProductVersion"):
                    if key in table.entries:
                        assert table.entries[key] == b"1.4.8"
                        pos = table.entries_offsets[key][1]
                        data[pos:pos + 10] = "1.0.0".encode("utf-16le")
    for fixed in pe.VS_FIXEDFILEINFO:
        pos = fixed.get_file_offset()
        for field in ("FileVersionMS", "ProductVersionMS"):
            struct.pack_into("<I", data, pos + fixed.__field_offsets__[field], 1 << 16)
        for field in ("FileVersionLS", "ProductVersionLS"):
            struct.pack_into("<I", data, pos + fixed.__field_offsets__[field], 0)
    updated = pefile.PE(data=bytes(data))
    checksum_offset = updated.OPTIONAL_HEADER.get_field_absolute_offset("CheckSum")
    struct.pack_into("<I", data, checksum_offset, updated.generate_checksum())
    # Verify the exact byte ranges modified: no native instruction changes.
    allowed = [(OFFSET, OFFSET + CAPACITY), (LENGTH_OFFSET, LENGTH_OFFSET + 8),
               (checksum_offset, checksum_offset + 4)]
    resource = pe.OPTIONAL_HEADER.DATA_DIRECTORY[2]
    resource_offset = pe.get_offset_from_rva(resource.VirtualAddress)
    allowed.append((resource_offset, resource_offset + resource.Size))
    assert all(any(lo <= i < hi for lo, hi in allowed)
               for i, (a, b) in enumerate(zip(before, data)) if a != b)
    assert brotli.decompress(data[OFFSET:OFFSET + len(packed)]).decode("utf-8") == source
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(data)
    output.with_suffix(".frontend.js").write_text(source, encoding="utf-8")
    report = dict(version="1.0.0", bot_version="8.58", project=PROJECT,
                  input_sha256=SOURCE_SHA256, output_sha256=hashlib.sha256(data).hexdigest(),
                  frontend_sha256=hashlib.sha256(source.encode()).hexdigest(),
                  automatic_updates=False, native_code_unchanged=True,
                  backend="pinned upstream 1.4.8 binary; unpublished Rust source")
    output.with_suffix(".build.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("original")
    parser.add_argument("output")
    args = parser.parse_args()
    build(args.original, args.output)
