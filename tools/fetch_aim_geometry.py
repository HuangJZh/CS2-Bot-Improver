"""Download pinned Awpy assets, verify archive hash, extract only named meshes."""
import argparse
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--release', required=True, type=int)
    parser.add_argument('--expected-patch', required=True)
    parser.add_argument('--out', required=True, type=Path)
    parser.add_argument('--maps', nargs='+', default=['de_anubis', 'de_dust2', 'de_nuke'])
    args = parser.parse_args()
    root = args.out / str(args.release)
    root.mkdir(parents=True, exist_ok=True)
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
    release = None

    def asset_url(name):
        nonlocal release
        if release is None:
            url = f'https://api.github.com/repos/pnxenopoulos/awpy-data/releases/tags/{args.release}'
            release = json.load(opener.open(url, timeout=20))
        return next(a['browser_download_url'] for a in release['assets'] if a['name'] == name)

    manifest_path = root / 'manifest.json'
    if not manifest_path.exists():
        manifest_path.write_bytes(opener.open(asset_url('manifest.json'), timeout=20).read())
    manifest = json.loads(manifest_path.read_bytes())
    if manifest['patch_version'] != args.expected_patch:
        raise ValueError('Map/demo patch mismatch')
    expected = manifest['artifacts']['geometry.zip']
    archive = root / 'geometry.zip'

    def valid(path):
        return (path.exists() and path.stat().st_size == expected['bytes']
                and hashlib.sha256(path.read_bytes()).hexdigest() == expected['sha256'])

    if not valid(archive):
        partial = root / 'geometry.zip.part'
        with opener.open(asset_url('geometry.zip'), timeout=30) as response, partial.open('wb') as out:
            while block := response.read(1024 * 1024):
                out.write(block)
        if not valid(partial):
            raise ValueError('Geometry checksum mismatch')
        partial.replace(archive)
    with zipfile.ZipFile(archive) as source:
        for map_name in args.maps:
            if map_name not in manifest['maps']:
                raise ValueError('Map missing from manifest')
            matches = [n for n in source.namelist() if Path(n).name == map_name + '.mesh']
            if len(matches) != 1:
                raise ValueError('Missing or ambiguous mesh')
            target = root / (map_name + '.mesh')
            target.write_bytes(source.read(matches[0]))
            print(target.name, hashlib.sha256(target.read_bytes()).hexdigest())


if __name__ == '__main__':
    main()
