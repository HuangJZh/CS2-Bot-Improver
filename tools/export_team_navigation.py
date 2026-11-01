"""Export current installed CS2 navigation adjacency; never use pre-aim targets as walk points."""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path
from awpy import NavMesh


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest().upper()


def export(map_name, archive, nav_path, output):
    nav = NavMesh(nav_path)
    areas = [nav.area(int(i)) for i in nav.areas['area_id']]
    # Hull 0 is the standing-player graph. Keep native directed connections;
    # native MoveTo decides how to execute each transition.
    areas = [a for a in areas if a['hull_index'] == 0]
    valid = {a['area_id'] for a in areas}
    data = {'Map': map_name, 'SourceMapSha256': digest(archive), 'SourceNavSha256': digest(nav_path),
            'Areas': [{'Id': a['area_id'], 'Position': a['centroid'], 'Corners': a['corners'],
                       'Neighbors': [n for n in a['connections'] if n in valid]} for a in areas]}
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(data, separators=(',', ':')), encoding='utf-8')
    print(map_name, 'areas', len(areas), 'edges', sum(len(a['Neighbors']) for a in data['Areas']))


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--maps', nargs='+', required=True)
    p.add_argument('--game', type=Path, required=True)
    p.add_argument('--extractor', type=Path, required=True)
    p.add_argument('--scratch', type=Path, required=True)
    p.add_argument('--out', type=Path, required=True)
    args = p.parse_args()
    for name in args.maps:
        if not name.startswith('de_') or '/' in name or '\\' in name:
            raise ValueError('Invalid map name')
        archive = args.game / 'game/csgo/maps' / (name + '.vpk')
        subprocess.run([str(args.extractor), '-i', str(archive), '-e', 'nav', '-o', str(args.scratch)], check=True,
                       stdout=subprocess.DEVNULL)
        export(name, archive, args.scratch / 'maps' / (name + '.nav'), args.out / (name + '.json'))
