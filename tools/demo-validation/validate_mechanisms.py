import json, pathlib, statistics, collections, math
import pandas as pd
from demoparser2 import DemoParser

DEMOS = [
    r'E:\csdemo\extracted\aurora-vs-falcons-m2-dust2.dem',
    r'E:\csdemo\extracted2\vitality-vs-mouz-m1-nuke.dem',
    r'E:\csdemo\extracted3\vitality-vs-natus-vincere-m2-mirage.dem',
]
HG = {'head': 1, 'chest': 2, 'stomach': 3, 'left_arm': 4, 'right_arm': 5, 'left_leg': 6, 'right_leg': 7}
BINS = [(0, 800), (800, 1500), (1500, 2500), (2500, 4000), (4000, 100000)]
REACTION_TICKS = 24  # ~0.375 s at 64 tick
PROPS = ['is_alive', 'team_num', 'health', 'X', 'Y', 'Z', 'yaw', 'pitch']
reaction = {kind: {i: {'events': 0, 'reacted': 0, 'aligned': 0, 'missed': 0} for i in range(len(BINS))}
            for kind in ['gunfire']}
hitgroup = collections.Counter()
weapon_hits = collections.defaultdict(lambda: collections.Counter())
shots = 0
events_count = 0


def wrap(deg):
    return (deg + 540.0) % 360.0 - 180.0


for path in DEMOS:
    p = DemoParser(path)
    fire = p.parse_event('weapon_fire')
    hurt = p.parse_event('player_hurt')
    if isinstance(fire, list) or fire is None or len(fire) == 0:
        continue
    # one event per shooter per 0.25 s so full-auto doesn't dominate the sample
    fire = fire.sort_values('tick')
    selected = []
    last = {}
    for _, row in fire.iterrows():
        sid = str(row['user_steamid'])
        t = int(row['tick'])
        if sid not in last or t - last[sid] > 16:
            selected.append((t, sid, row.get('weapon', ''), bool(row.get('silenced', False))))
            last[sid] = t
    shots += len(selected)
    ticks = set()
    for t, _, _, _ in selected:
        for off in (-1, 0, REACTION_TICKS):
            if t + off >= 0:
                ticks.add(t + off)
    if not ticks:
        continue
    df = p.parse_ticks(PROPS, ticks=sorted(ticks))
    df = df[df['is_alive'] == True].copy()
    idx = {}
    for _, r in df.iterrows():
        idx[(int(r['tick']), str(r['steamid']))] = r
    for t, sid, weapon, silenced in selected:
        src = idx.get((t, sid))
        if src is None:
            continue
        if src['team_num'] not in (2, 3):
            continue
        for (tick, lsid), lst in idx.items():
            if tick != t or lsid == sid or lst['team_num'] == src['team_num']:
                continue
            post = idx.get((t + REACTION_TICKS, lsid))
            if post is None:
                continue
            dx = float(src['X']) - float(lst['X'])
            dy = float(src['Y']) - float(lst['Y'])
            dist = (dx * dx + dy * dy) ** .5
            b = None
            for i, (lo, hi) in enumerate(BINS):
                if lo <= dist < hi:
                    b = i
                    break
            if b is None:
                continue
            desired = math.atan2(dy, dx) * 180.0 / math.pi
            pre_diff = abs(wrap(desired - float(lst['yaw'])))
            post_diff = abs(wrap(desired - float(post['yaw'])))
            cell = reaction['gunfire'][b]
            cell['events'] += 1
            if pre_diff < 15.0:
                cell['aligned'] += 1
            elif post_diff < 30.0 and post_diff < pre_diff - 10.0:
                cell['reacted'] += 1
            else:
                cell['missed'] += 1
    if hurt is not None and not isinstance(hurt, list):
        for _, r in hurt.iterrows():
            hg = HG.get(str(r.get('hitgroup', '')).lower(), 0)
            hitgroup[hg] += 1
            weapon_hits[str(r.get('weapon', 'unknown'))][hg] += 1
    events_count += 1

result = {
    'demos': [pathlib.Path(x).name for x in DEMOS],
    'reaction_ticks': REACTION_TICKS,
    'gunfire_selected_events': shots,
    'gunfire_reaction': {},
    'hitgroup_total': dict(hitgroup),
    'headshot_fraction_by_weapon': {},
    'silent_calibration': None,
}
for i, (lo, hi) in enumerate(BINS):
    c = reaction['gunfire'][i]
    total = c['events']
    result['gunfire_reaction'][f'{lo}-{hi}'] = {
        'events': total,
        'reacted': c['reacted'],
        'already_aligned': c['aligned'],
        'missed': c['missed'],
        'reacted_pct': round(100.0 * c['reacted'] / total, 2) if total else None,
        'aligned_pct': round(100.0 * c['aligned'] / total, 2) if total else None,
    }
for weapon, counts in weapon_hits.items():
    total = sum(counts.values())
    if total >= 20:
        result['headshot_fraction_by_weapon'][weapon] = {
            'hits': total,
            'head_pct': round(100.0 * counts.get(1, 0) / total, 2),
            'chest_pct': round(100.0 * counts.get(2, 0) / total, 2),
        }
sc = pathlib.Path(r'E:\csdemo\analysis\silent-calibration.json')
if sc.exists():
    result['silent_calibration'] = json.loads(sc.read_text(encoding='utf-8'))

out = pathlib.Path(r'E:\csdemo\analysis\mechanism-validation.json')
out.write_text(json.dumps(result, indent=2), encoding='utf-8')
print(json.dumps(result, indent=2))
print('saved', out)
