"""Join candidate contacts to gun events while keeping attribution and censoring explicit."""
import argparse,json,math
from pathlib import Path
import numpy as np
import pandas as pd
from analyze_demo_aim import group

def enrich(match,candidates_dir=None):
 destination=candidates_dir or match
 path=destination/'contact-candidates.parquet'
 if not path.exists():return
 c=pd.read_parquet(path);m=json.loads((match/'manifest.json').read_text());interval=m['tick_interval_seconds']
 fires=pd.read_parquet(match/'weapon_fire.parquet');fires=fires[fires.weapon.map(group)!='other']
 hurts=pd.read_parquet(match/'player_hurt.parquet');hurts=hurts[hurts.weapon.map(group)!='other']
 deaths=pd.read_parquet(match/'player_death.parquet')
 for f in [fires,hurts,deaths]:
  for col in ['user_steamid','attacker_steamid']:
   if col in f:f[col]=f[col].astype(str)
 starts=c.start_tick.astype(int).unique().tolist();cols=['tick','steamid','X','Y','Z','pitch','yaw','duck_amount','active_weapon_name','is_scoped','fov']
 frames=pd.read_parquet(match/'frames.parquet',columns=cols,filters=[('tick','in',starts)]).set_index(['tick','steamid'])
 rows=[]
 for r in c.to_dict('records'):
  start,end=int(r['start_tick']),int(r['end_tick']);observer,target=str(r['observer']),str(r['target'])
  fire=fires[(fires.user_steamid==observer)&(fires.tick>=start)&(fires.tick<=end)]
  pair=hurts[(hurts.attacker_steamid==observer)&(hurts.user_steamid==target)]
  damage=pair[(pair.tick>=start)&(pair.tick<=end)]
  first_fire=int(fire.tick.min()) if len(fire) else None;first_damage=int(damage.tick.min()) if len(damage) else None
  r['first_unattributed_fire_ms']=(first_fire-start)*interval*1000 if first_fire is not None else None
  r['first_pair_damage_ms_candidate']=(first_damage-start)*interval*1000 if first_damage is not None else None
  r['pair_damage_count']=len(damage);r['unattributed_fire_count']=len(fire);r['no_damage']=not len(damage);r['no_observer_fire']=not len(fire)
  r['prior_pair_damage_500ms']=bool(((pair.tick<start)&(pair.tick>=start-round(.5/interval))).any())
  incoming=hurts[(hurts.attacker_steamid==target)&(hurts.user_steamid==observer)]
  r['prior_incoming_pair_damage_500ms']=bool(((incoming.tick<start)&(incoming.tick>=start-round(.5/interval))).any())
  r['competing_candidate_targets_at_first_fire']=int(((c.observer==observer)&(c.target!=target)&(c.start_tick<=first_fire)&(c.end_tick>=first_fire)).sum()) if first_fire is not None else 0
  r['first_fire_pair_damage_same_or_next_tick']=bool(((damage.tick>=first_fire)&(damage.tick<=first_fire+1)).any()) if first_fire is not None else None
  # Does not call a shot a miss: the shot could have targeted a different enemy.
  r['fire_target_unconfirmed']=True
  dead=deaths[(deaths.user_steamid==observer)&(deaths.tick>=start)&(deaths.tick<=end)]
  target_deaths=deaths[(deaths.user_steamid==target)&(deaths.attacker_steamid==observer)&(deaths.tick>=start)&(deaths.tick<=end)]
  r['target_kill_weapon']=target_deaths.iloc[0].weapon if len(target_deaths) else None
  r['target_killed_with_gun']=bool(len(target_deaths) and group(str(target_deaths.iloc[0].weapon))!='other')
  r['observer_death_weapon']=dead.iloc[0].weapon if len(dead) else None
  r['observer_died']=bool(len(dead));r['observer_killed_by_this_target']=bool((dead.attacker_steamid==target).any())
  r['no_damage_before_observer_death']=r['observer_died'] and r['no_damage']
  a=frames.loc[(start,int(observer))];b=frames.loc[(start,int(target))]
  eye=np.array([a.X,a.Y,a.Z+64-18*a.duck_amount]);head=np.array([b.X,b.Y,b.Z+64-18*b.duck_amount]);delta=head-eye
  pitch,yaw=np.radians([a.pitch,a.yaw]);forward=np.array([math.cos(pitch)*math.cos(yaw),math.cos(pitch)*math.sin(yaw),-math.sin(pitch)])
  r['initial_approx_head_angle_deg']=float(np.degrees(np.arccos(np.clip(delta@forward/max(np.linalg.norm(delta),1e-6),-1,1))))
  r['weapon_name_at_start']=a.active_weapon_name;r['scoped_at_start']=bool(a.is_scoped);r['fov_field_at_start']=float(a.fov)
  rows.append(r)
 out=pd.DataFrame(rows);out.to_parquet(path,index=False);out.to_csv(destination/'contact-candidates.csv',index=False)
 summary=json.loads((destination/'contact-candidate-summary.json').read_text());summary.update(gun_damage_only=True,no_damage_contacts=int(out.no_damage.sum()),no_fire_contacts=int(out.no_observer_fire.sum()),no_damage_before_observer_death=int(out.no_damage_before_observer_death.sum()),left_censored=int(out.left_censored.sum()),right_censored=int(out.right_censored.sum()),attribution_note='Viewer firing remains unattributed. Same-tick damage is evidence, not proof of intended target or a missed shot.')
 (destination/'contact-candidate-summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8');print(match.name,summary,flush=True)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('matches',type=Path);p.add_argument('--candidate-root',type=Path);a=p.parse_args()
 for m in sorted(a.matches.iterdir()):
  destination=a.candidate_root/m.name if a.candidate_root else m
  if (destination/'contact-candidates.parquet').exists():enrich(m,destination)
