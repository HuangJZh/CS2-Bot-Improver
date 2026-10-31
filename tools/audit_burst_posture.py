"""Audit native stance intent during recorded AK bursts; not tactical calibration."""
import argparse
import collections
import json
from pathlib import Path


def audit(path):
    rows = [json.loads(line) for line in path.read_text().splitlines()]
    fires = [r for r in rows if r.get('kind') == 'weapon_fire_callback'
             and r.get('event_weapon') == 'weapon_ak47']
    if len(fires) < 20:
        raise ValueError('Insufficient AK fire events')
    actor = fires[0]['slot']
    bursts, current = [], []
    for row in fires:
        if row['slot'] != actor:
            continue
        if current and row['time'] - current[-1]['time'] > .5:
            bursts.append(current)
            current = []
        current.append(row)
    if current:
        bursts.append(current)
    results = []
    for burst in bursts:
        if len(burst) < 20:
            continue
        start = burst[0]['time'] + rows[0]['tick_interval']
        end = burst[-1]['time']
        frames = [r for r in rows if r.get('kind') == 'server_tick_observation'
                  and r.get('slot') == actor and start <= r['time'] <= end]
        if not frames or any('native_crouching' not in r for r in frames):
            raise ValueError('Missing stance signal')
        states = [r['native_crouching'] for r in frames]
        heights = [r['view_offset_z'] for r in frames]
        results.append(dict(shots=len(burst), frames=len(frames),
                            intent_switches=sum(a != b for a, b in zip(states, states[1:])),
                            crouch_intent_counts=dict(collections.Counter(map(str, states))),
                            eye_height_min=min(heights), eye_height_max=max(heights),
                            scope='Scripted fixture: intent persistence, not tactical choice or rank.'))
    if not results:
        raise ValueError('No complete burst')
    return results


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('capture', type=Path)
    args = parser.parse_args()
    result = audit(args.capture)
    args.capture.with_suffix('.posture-audit.json').write_text(json.dumps(result, indent=2), encoding='utf-8')
    print(json.dumps(result, indent=2))
