"""Audit closed Bot JSONL captures; this is not a skill or shot-subtick verifier."""
import argparse
import collections
import json
from pathlib import Path
import numpy as np


def audit(path):
    rows = [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines() if line.strip()]
    if not rows or rows[0].get('schema') != 'bot-aim-telemetry-v1':
        raise ValueError('Capture schema missing')
    counts = collections.Counter(row.get('kind') for row in rows)
    seen, previous, tracked = set(), {}, {}
    for row in rows:
        if row.get('kind') != 'server_tick_observation':
            continue
        key = (row['tick'], row['slot'], row['pawn'])
        if key in seen:
            raise ValueError('Duplicate player tick')
        seen.add(key)
        owner = (row['slot'], row['pawn'])
        if owner in previous and row['tick'] < previous[owner]:
            raise ValueError('Non-monotonic player ticks')
        previous[owner] = row['tick']
        if row.get('phase') == 'tracking' and row.get('planned_yaw') is not None:
            error = (row['yaw'] - row['planned_yaw'] + 180) % 360 - 180
            tracked.setdefault(str(row['slot']), []).append(abs(error))
    return dict(
        file=path.name, schema='bot-aim-capture-audit-v1', rows=len(rows), kinds=dict(counts),
        tracking_yaw_error={slot: dict(samples=len(errors), median_deg=float(np.median(errors)),
                                      p95_deg=float(np.quantile(errors, .95)), max_deg=float(max(errors)))
                            for slot, errors in tracked.items()},
        limitations=['Angles refer to the server observation point, not raw usercmd or exact shot-subtick angles.',
                     'Controlled fixtures do not establish natural combat quality or rank strength.'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('folder', type=Path)
    args = parser.parse_args()
    summaries = [audit(path) for path in sorted(args.folder.glob('*.jsonl'))]
    (args.folder / 'capture-audits.json').write_text(json.dumps(summaries, indent=2), encoding='utf-8')
    print(json.dumps(summaries, indent=2))
