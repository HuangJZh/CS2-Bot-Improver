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
    guns = {'ak47','m4a1','m4a1_silencer','awp','ssg08','scar20','g3sg1','aug','sg556','galilar','famas',
            'glock','hkp2000','usp_silencer','p250','fiveseven','tec9','elite','cz75a','deagle','revolver',
            'mac10','mp9','mp7','mp5sd','ump45','p90','bizon','nova','xm1014','mag7','sawedoff','negev','m249'}
    fires = [r for r in rows if r.get('kind') == 'weapon_fire_callback'
             and str(r.get('event_weapon','')).removeprefix('weapon_') in guns]
    blocked = [r for r in fires if r.get('allow_fire') is False]
    unknown = sum(r.get('allow_fire') is None for r in fires)
    gate_status = ('blocked-state-fire-callbacks' if blocked else 'no-classified-gun-fire' if not fires
                   else 'insufficient-gate-state' if unknown else 'no-blocked-state-callbacks-observed')
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
        fire_gate=dict(status=gate_status,gun_fire_callbacks=len(fires),unknown_decision_callbacks=unknown,
                       blocked_state_callbacks=len(blocked),blocked_and_suppression_owned=sum(bool(r.get('model_fire_suppression_owned')) for r in blocked),
                       examples=[{k:r.get(k) for k in ['slot','time','event_weapon','phase','fire_reason','model_fire_suppression_owned']} for r in blocked[:10]]),
        tracking_yaw_error={slot: dict(samples=len(errors), median_deg=float(np.median(errors)),
                                      p95_deg=float(np.quantile(errors, .95)), max_deg=float(max(errors)))
                            for slot, errors in tracked.items()},
        limitations=['Angles refer to the server observation point, not raw usercmd or exact shot-subtick angles.',
                     'Fire callback state may differ from state at the actual shot; contradictions require execution-order review. Zero shots is not a passed gating test.',
                     'Controlled fixtures do not establish natural combat quality or rank strength.'])


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('folder', type=Path)
    args = parser.parse_args()
    summaries = [audit(path) for path in sorted(args.folder.glob('*.jsonl'))]
    (args.folder / 'capture-audits.json').write_text(json.dumps(summaries, indent=2), encoding='utf-8')
    print(json.dumps(summaries, indent=2))
