"""Full-tick pair contacts from APPROXIMATE geometry/FOV/smoke. No calibrated TTD claims."""
import argparse,json,math,time,hashlib
from functools import lru_cache
from pathlib import Path
import numpy as np
import pandas as pd
from awpy import VisibilityChecker
from probe_demo_visibility import in_fov,intersects_smoke,smoke_intervals
from contact_segments import segments
from analyze_demo_aim import group
from enrich_contact_candidates import enrich
from round_phases import live_round_intervals,live_mask
from export_match_evidence import digest
COLS=["tick","steamid","X","Y","Z","pitch","yaw","duck_amount","fov","team_num","is_alive","game_time","total_rounds_played","is_warmup_period","is_freeze_period","flash_duration"]

def build(match,assets,names,out=None):
 destination=(out/match.name) if out else match
 destination.mkdir(parents=True,exist_ok=True)
 cache_dir=destination/"visibility-cache";cache_dir.mkdir(exist_ok=True)
 stamp=hashlib.sha256(Path(__file__).read_bytes()+Path(__file__).with_name("probe_demo_visibility.py").read_bytes())
 for name in ["frames.parquet","smokegrenade_detonate.parquet","smokegrenade_expired.parquet","round_start.parquet","round_end.parquet"]:stamp.update(digest(match/name).encode())
 stamp.update(digest(assets/(json.loads((match/"manifest.json").read_text())["header"]["map_name"]+".mesh")).encode());cache_stamp=stamp.hexdigest()
 started=time.monotonic();meta=json.loads((match/"manifest.json").read_text());manifest=json.loads((assets/"manifest.json").read_text())
 if manifest["patch_version"].replace(".","")!=meta["header"]["patch_version"]:raise ValueError("Map patch mismatch")
 vc=VisibilityChecker(assets/(meta["header"]["map_name"]+".mesh"))
 @lru_cache(maxsize=100000)
 def clear_ray(start,end):return vc.is_visible(start,end)
 players=pd.read_parquet(match/"players.parquet")
 frames=pd.read_parquet(match/"frames.parquet",columns=COLS);traces={int(s):g.reset_index(drop=True) for s,g in frames.groupby("steamid",sort=True)};del frames
 clock=pd.read_parquet(match/"clock.parquet");ticks=clock.tick.to_numpy();rounds=clock.total_rounds_played.to_numpy()
 for f in traces.values():
  if not np.array_equal(ticks,f.tick.to_numpy()):raise ValueError("Missing player snapshots: must not silently interpolate")
 events={n:pd.read_parquet(match/(n+".parquet")) for n in ["weapon_fire","player_hurt","player_death"]}
 events["weapon_fire"]=events["weapon_fire"][events["weapon_fire"].weapon.map(group)!="other"]
 boundaries=[]
 for n in ["round_start","round_end"]:boundaries+=pd.read_parquet(match/(n+".parquet")).tick.astype(int).tolist()
 smoke=smoke_intervals(pd.read_parquet(match/"smokegrenade_detonate.parquet"),pd.read_parquet(match/"smokegrenade_expired.parquet"),boundaries,int(ticks[-1]))
 phase=(ticks>=meta["competitive_start_candidate_tick"]) & ~clock.is_warmup_period.to_numpy(bool) & ~clock.is_freeze_period.to_numpy(bool)
 intervals=live_round_intervals(clock,pd.read_parquet(match/"round_freeze_end.parquet"),pd.read_parquet(match/"round_end.parquet"),meta["competitive_start_candidate_tick"])
 live=live_mask(ticks,intervals)
 (destination/"live-round-intervals.json").write_text(json.dumps(intervals,indent=2),encoding="utf-8")
 player_names={int(r.steamid):r.name for r in players.itertuples()};rows=[];lost_ticks=round(.25/meta["tick_interval_seconds"])
 for observer,a in traces.items():
  if names and player_names[observer] not in names:continue
  eye=a[["X","Y","Z"]].to_numpy(float);eye[:,2]+=64-18*a.duck_amount.to_numpy(float)
  apitch=a.pitch.to_numpy(float);ayaw=a.yaw.to_numpy(float)
  pitch,yaw=np.radians(apitch),np.radians(ayaw)
  forward=np.column_stack((np.cos(pitch)*np.cos(yaw),np.cos(pitch)*np.sin(yaw),-np.sin(pitch)))
  right=np.column_stack((-np.sin(yaw),np.cos(yaw),np.zeros(len(a))));up=np.cross(right,forward)
  base=a.fov.to_numpy(float);base=np.where(base<=0,90,base);vertical=np.tan(np.radians(base)/2)/(4/3)
  for target,b in traces.items():
   if observer==target:continue
   eligible=phase & a.is_alive.to_numpy(bool) & b.is_alive.to_numpy(bool) & (a.team_num.to_numpy()!=b.team_num.to_numpy()) & a.team_num.isin([2,3]).to_numpy() & b.team_num.isin([2,3]).to_numpy()
   ray_eligible=eligible.copy();eligible &= live
   if not ray_eligible.any():continue
   byaw=b.yaw.to_numpy(float)
   origin=b[["X","Y","Z"]].to_numpy(float);height=64-18*b.duck_amount.to_numpy(float);center=origin.copy();center[:,2]+=height*.75
   d=center-eye;depth=np.einsum('ij,ij->i',d,forward);h=np.abs(np.einsum('ij,ij->i',d,right));v=np.abs(np.einsum('ij,ij->i',d,up))
   broad=ray_eligible & (depth>0) & (h<=vertical*(16/9)*depth+40) & (v<=vertical*depth+40)
   visible=np.zeros(len(a),bool);aspect_uncertain=np.zeros(len(a),bool)
   cache=cache_dir/(cache_stamp[:16]+f'-{observer}-{target}.npz')
   if cache.exists():
    with np.load(cache,allow_pickle=False) as stored:
     if stored['stamp'].item()!=cache_stamp or not np.array_equal(stored['ticks'],ticks) or np.any(ray_eligible & ~stored['coverage']):raise ValueError('Cache coverage mismatch')
     visible=stored['visible'];aspect_uncertain=stored['aspect_uncertain']
   else:
    for i in np.flatnonzero(broad):
     by=math.radians(float(byaw[i]));side=np.array([math.sin(by),-math.cos(by),0])*7
     chest=origin[i]+[0,0,height[i]*.75]
     points=[origin[i]+[0,0,height[i]],chest,origin[i]+[0,0,height[i]*.5],chest+side,chest-side]
     for point in points:
      narrow=in_fov(eye[i],point,apitch[i],ayaw[i],base[i],4/3)
      if not narrow and not in_fov(eye[i],point,apitch[i],ayaw[i],base[i],16/9):continue
      if not clear_ray(tuple(eye[i]),tuple(point)):continue
      if not narrow:aspect_uncertain[i]=True;continue
      blocked=any(begin<=ticks[i]<end and intersects_smoke(eye[i],point,c,155*min(1,(ticks[i]-begin)*meta["tick_interval_seconds"]/.7)) for begin,end,c in smoke)
      if not blocked:visible[i]=True;break
    partial=cache.with_suffix('.partial')
    with partial.open('wb') as f:np.savez_compressed(f,stamp=cache_stamp,ticks=ticks,coverage=ray_eligible,visible=visible,aspect_uncertain=aspect_uncertain)
    partial.replace(cache)
   contacts=segments(ticks,rounds,eligible,visible,a.is_alive.to_numpy(bool),b.is_alive.to_numpy(bool),lost_ticks)
   fires=events["weapon_fire"];fires=fires[fires.user_steamid.astype(str)==str(observer)]
   hurts=events["player_hurt"];hurts=hurts[hurts.weapon.map(group)!="other"];hurts=hurts[(hurts.attacker_steamid.astype(str)==str(observer))&(hurts.user_steamid.astype(str)==str(target))]
   deaths=events["player_death"]
   for row in contacts:
    start,end=row['start_tick'],row['end_tick'];idx=int(np.searchsorted(ticks,start));window=fires[(fires.tick>=start)&(fires.tick<=end)];damage=hurts[(hurts.tick>=start)&(hurts.tick<=end)]
    killed=deaths[(deaths.tick>=start)&(deaths.tick<=end)&(deaths.user_steamid.astype(str)==str(target))&(deaths.attacker_steamid.astype(str)==str(observer))]
    died=deaths[(deaths.tick>=start)&(deaths.tick<=end)&(deaths.user_steamid.astype(str)==str(observer))]
    row.update(observer=str(observer),observer_name=player_names[observer],target=str(target),target_name=player_names[target],distance_units=float(np.linalg.norm(center[idx]-eye[idx])),
     raw_flash_duration_start=float(a.flash_duration.iloc[idx]),visible_tick_fraction=row['visible_ticks']/max(1,row['last_visible_tick']-start+1),aspect_uncertain_ticks=int(aspect_uncertain[(ticks>=start)&(ticks<=end)].sum()),
     first_unattributed_fire_ms=float((window.tick.min()-start)*meta['tick_interval_seconds']*1000) if len(window) else None,
     first_pair_damage_ms_candidate=float((damage.tick.min()-start)*meta['tick_interval_seconds']*1000) if len(damage) else None,
     unattributed_fire_count=len(window),pair_damage_count=len(damage),target_killed_by_observer=bool(len(killed)),observer_died=bool(len(died)),
     no_damage=not bool(len(damage)),fire_target_unconfirmed=True,visibility_status='unvalidated_candidate')
    rows.append(row)
   print(match.name,player_names[observer],player_names[target],'broad',int(broad.sum()),'visible',int(visible.sum()),'contacts',len(contacts),flush=True)
 result=pd.DataFrame(rows);result.to_parquet(destination/'contact-candidates.parquet',index=False);result.to_csv(destination/'contact-candidates.csv',index=False)
 summary=dict(status='unvalidated_candidates',rows=len(result),observer_names=names,elapsed_seconds=time.monotonic()-started,lost_tolerance_ticks=lost_ticks,phase_model="freeze_end_to_round_end",visibility_cache_stamp=cache_stamp,
  note='Full-tick scan, not damage-conditioned. No-damage and no-fire windows retained. First visible is approximate; viewer fire is not assigned to this target. Do not use as calibrated TTD.')
 (destination/'contact-candidate-summary.json').write_text(json.dumps(summary,indent=2),encoding='utf-8');print(summary,flush=True)
 enrich(match,destination)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('matches',type=Path);p.add_argument('--assets',type=Path,required=True);p.add_argument('--maps',nargs='*');p.add_argument('--out',type=Path);p.add_argument('--viewers',nargs='*',default=['NiKo','kyousuke','TeSeS','m0NESY']);a=p.parse_args()
 for m in sorted(a.matches.iterdir()):
  if (m/'manifest.json').exists() and (not a.maps or any(n in m.name for n in a.maps)):build(m,a.assets,a.viewers,a.out)
