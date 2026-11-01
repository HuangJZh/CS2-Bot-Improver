"""Replace one entry in an unsigned, fully embedded VPK; retain other assets."""
import argparse
import struct
import zlib
from pathlib import Path


def read_entries(blob):
    signature, version, tree_size = struct.unpack_from('<III', blob)
    if signature != 0x55AA1234 or version not in (1, 2):
        raise ValueError('Unsupported VPK header')
    header = 12 if version == 1 else 28
    if version == 2 and any(struct.unpack_from('<III', blob, 16)):
        raise ValueError('Signed/checksummed archives require their original packer')
    pos = header
    end = header + tree_size

    def word():
        nonlocal pos
        stop = blob.index(b'\0', pos, end)
        value = blob[pos:stop].decode('utf-8')
        pos = stop + 1
        return value

    entries = {}
    while ext := word():
        while folder := word():
            while name := word():
                crc, preload, archive, offset, length, term = struct.unpack_from('<IHHIIH', blob, pos)
                pos += 18
                if archive != 0x7FFF or term != 0xFFFF:
                    raise ValueError('Only fully embedded archives are supported')
                data = blob[pos:pos + preload] + blob[end + offset:end + offset + length]
                pos += preload
                if len(data) != preload + length or zlib.crc32(data) != crc:
                    raise ValueError('Invalid entry length or CRC')
                path = ('' if folder == ' ' else folder + '/') + name + '.' + ext
                entries[path] = data
    return entries


def pack(entries):
    groups = {}
    for path, data in entries.items():
        stem, ext = path.rsplit('.', 1)
        folder, _, name = stem.rpartition('/')
        groups.setdefault(ext, {}).setdefault(folder or ' ', {})[name] = data
    tree = bytearray()
    payload = bytearray()
    for ext, folders in sorted(groups.items()):
        tree.extend(ext.encode() + b'\0')
        for folder, files in sorted(folders.items()):
            tree.extend(folder.encode() + b'\0')
            for name, data in sorted(files.items()):
                tree.extend(name.encode() + b'\0')
                tree.extend(struct.pack('<IHHIIH', zlib.crc32(data), 0, 0x7FFF, len(payload), len(data), 0xFFFF))
                payload.extend(data)
            tree.extend(b'\0')
        tree.extend(b'\0')
    tree.extend(b'\0')
    return struct.pack('<7I', 0x55AA1234, 2, len(tree), len(payload), 0, 0, 0) + tree + payload


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('archive', type=Path)
    parser.add_argument('entry')
    parser.add_argument('replacement', type=Path)
    parser.add_argument('output', type=Path)
    args = parser.parse_args()
    entries = read_entries(args.archive.read_bytes())
    if args.entry not in entries:
        raise ValueError('Replacement entry does not exist')
    entries[args.entry] = args.replacement.read_bytes()
    result = pack(entries)
    if read_entries(result) != entries:
        raise ValueError('Archive round-trip validation failed')
    args.output.write_bytes(result)
