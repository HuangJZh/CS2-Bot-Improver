"""Read-only demo evidence for sound coverage and signed spray-view variability.

Camera movement includes tracking/turning/recoil control; it is NOT a recoil
pattern or proof that an opponent was audible. Does not modify game files.
"""
import argparse
import collections
import json
import math
from pathlib import Path
import statistics
from demoparser2 import DemoParser


def audit(path):
    parser = DemoParser(str(path))
    events = set(parser.list_game_events())
    sounds = parser.parse_event('player_sound') if 'player_sound' in events else None
    steps = sounds[sounds['step'] == True] if sounds is not None else None
    fire = parser.parse_event('weapon_fire')
    fire['weapon'] = fire['weapon'].str.replace(r'^weapon_', '', regex=True)
    fire = fire[fire['weapon'].isin(['ak47', 'm4a1', 'm4a1_silencer'])].sort_values('tick')
    bursts, current = [], {}
    for row in fire.itertuples():
        key = (str(row.user_steamid), row.weapon)
        tick = int(row.tick)
        prior = current.get(key)
        if prior is None or tick - prior[-1] > 16:
            if prior and len(prior) >= 3:
                bursts.append((key, prior))
            current[key] = [tick]
        elif tick != prior[-1]:
            prior.append(tick)
    bursts.extend((k, v) for k, v in current.items() if len(v) >= 3)
    ticks = sorted({t for _, ts in bursts for t in ts[:10]})
    view = parser.parse_ticks(['yaw', 'pitch'], ticks=ticks) if ticks else None
    lookup = {} if view is None else {(int(r.tick), str(r.steamid)): float(r.yaw) for r in view.itertuples()}
    deltas = collections.defaultdict(list)
    for (sid, weapon), ts in bursts:
        start = lookup.get((ts[0], sid))
        if start is None:
            continue
        for i, tick in enumerate(ts[:10]):
            value = lookup.get((tick, sid))
            if value is not None and math.isfinite(value):
                deltas[i+1].append((value - start + 180) % 360 - 180)
    return {
        'demo': path.name,
        'legacy_player_footstep_present': 'player_footstep' in events,
        'player_sound_events': 0 if sounds is None else len(sounds),
        'player_sound_step_events': 0 if steps is None else len(steps),
        'step_radii': {} if steps is None else {str(k): int(v) for k, v in steps['radius'].value_counts().items()},
        'rifle_bursts_ge3': len(bursts),
        'view_yaw_by_shot': {str(i): {
            'n': len(v), 'signed_mean_deg': statistics.mean(v),
            'sd_deg': statistics.pstdev(v),
            'left_of_start': sum(x < -.1 for x in v),
            'right_of_start': sum(x > .1 for x in v),
        } for i, v in sorted(deltas.items())},
    }


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument('--root', type=Path, required=True)
    cli.add_argument('--output', type=Path, required=True)
    args = cli.parse_args()
    rows = []
    for path in sorted(args.root.glob('extracted*/*.dem')):
        row = audit(path)
        rows.append(row)
        print(json.dumps({'demo': row['demo'], 'steps': row['player_sound_step_events'],
                          'legacy_steps': row['legacy_player_footstep_present'],
                          'rifle_bursts': row['rifle_bursts_ge3']}, ensure_ascii=False), flush=True)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps({'demos': rows,
        'limits': 'Demo event presence is not a live event-emission guarantee. Camera yaw includes opponent tracking, own motion, turn/reposition and recoil control; signed means are not a recoil-control curve. No occlusion or per-listener audibility ground truth.'}, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
