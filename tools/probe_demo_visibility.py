"""Consistency probes at damage events; NOT verified first-visible timestamps."""
from pathlib import Path
import argparse,json,hashlib,math
import numpy as np
import pandas as pd
from awpy import VisibilityChecker
from analyze_demo_aim import group

def in_fov(eye,point,pitch,yaw,base_fov,aspect):
 p,y=np.radians([pitch,yaw]);forward=np.array([math.cos(p)*math.cos(y),math.cos(p)*math.sin(y),-math.sin(p)])
 right=np.array([-math.sin(y),math.cos(y),0]);up=np.cross(right,forward)
 d=point-eye;depth=float(d@forward)
 if depth<=0:return False
 base=math.tan(math.radians(base_fov)/2);vertical=base/(4/3)
 return abs(float(d@right))/depth<=vertical*aspect and abs(float(d@up))/depth<=vertical

def intersects_smoke(a,b,c,r):
 d=b-a;l=float(d@d)
 t=0 if l<1e-9 else np.clip(float((c-a)@d)/l,0,1)
 return float(np.linalg.norm(a+t*d-c))<r

def smoke_intervals(smoke,expired,boundaries,last_tick):
 intervals=[]
 for s in smoke.itertuples():
  # Missing expiration must not leak smoke into the next round or beyond the map.
  limits=[last_tick+1]
  future=expired[(expired.entityid==s.entityid)&(expired.tick>=s.tick)]
  if len(future):limits.append(int(future.tick.min()))
  future=smoke[(smoke.entityid==s.entityid)&(smoke.tick>s.tick)]
  if len(future):limits.append(int(future.tick.min()))
  later=[int(t) for t in boundaries if t>s.tick]
  if later:limits.append(min(later))
  intervals.append((s.tick,min(limits),np.array([s.x,s.y,s.z],float)))
 return intervals

def probe(match,assets):
 m=json.loads((match/"manifest.json").read_text());mappatch=json.loads((assets/"manifest.json").read_text())["patch_version"]
 if mappatch.replace(".","")!=m["header"]["patch_version"]:raise ValueError("Map/demo patch mismatch")
 mesh=assets/(m["header"]["map_name"]+".mesh");vc=VisibilityChecker(mesh)
 hurts=pd.read_parquet(match/"player_hurt.parquet");hurts=hurts[hurts.weapon.map(group)!="other"]
 hurts=hurts[(hurts.tick>=m["competitive_start_candidate_tick"]) & ~hurts.is_warmup_period.fillna(True)]
 wanted=set(hurts.tick.astype(int))
 # Partitioned selection avoids loading every frame as Python objects.
 frame=pd.read_parquet(match/"frames.parquet",filters=[("tick","in",list(wanted))]).set_index(["tick","steamid"])
 smoke=pd.read_parquet(match/"smokegrenade_detonate.parquet");expired=pd.read_parquet(match/"smokegrenade_expired.parquet")
 clock=pd.read_parquet(match/"clock.parquet")
 boundaries=[]
 for event in ["round_start","round_end"]:
  file=match/(event+".parquet")
  if file.exists():boundaries.extend(pd.read_parquet(file).tick.dropna().astype(int).tolist())
 intervals=smoke_intervals(smoke,expired,boundaries,int(clock.tick.max()))
 deaths=pd.read_parquet(match/"player_death.parquet")
 death_index={(int(d.tick),str(d.attacker_steamid),str(d.user_steamid)):d for d in deaths.itertuples()}
 rows=[]
 for e in hurts.itertuples():
  if pd.isna(e.attacker_steamid) or pd.isna(e.user_steamid):continue
  try:a=frame.loc[(e.tick,int(e.attacker_steamid))];b=frame.loc[(e.tick,int(e.user_steamid))]
  except KeyError:continue
  if a.team_num==b.team_num:continue
  origin=np.array([a.X,a.Y,a.Z],float);eye=origin+[0,0,64-18*float(a.duck_amount)]
  enemy=np.array([b.X,b.Y,b.Z],float);height=64-18*float(b.duck_amount)
  yaw=math.radians(float(b.yaw));right=np.array([math.sin(yaw),-math.cos(yaw),0])
  points=[enemy+[0,0,height],enemy+[0,0,height*.75],enemy+[0,0,height*.5],enemy+[0,0,height*.75]+right*7,enemy+[0,0,height*.75]-right*7]
  fov=float(a.fov);fov=90 if fov<=0 else fov
  clear_narrow=clear_wide=False;smoke_free=False
  for point in points:
   narrow=in_fov(eye,point,a.pitch,a.yaw,fov,4/3);wide=in_fov(eye,point,a.pitch,a.yaw,fov,16/9)
   if not wide:continue
   if not vc.is_visible(tuple(eye),tuple(point)):continue
   clear_wide=True;clear_narrow |= narrow
   blocked=any(begin<=e.tick<end and intersects_smoke(eye,point,center,155*min(1,(e.tick-begin)*m["tick_interval_seconds"]/.7)) for begin,end,center in intervals)
   smoke_free |= narrow and not blocked
  death=death_index.get((int(e.tick),str(e.attacker_steamid),str(e.user_steamid)))
  rows.append(dict(hitgroup=e.hitgroup,kill_event=death is not None,
    death_penetrated=getattr(death,"penetrated",None),death_thrusmoke=getattr(death,"thrusmoke",None),death_attackerblind=getattr(death,"attackerblind",None),tick=e.tick,attacker=str(e.attacker_steamid),victim=str(e.user_steamid),weapon=e.weapon,damage=e.dmg_health,
    static_clear_4_3=clear_narrow,static_clear_16_9=clear_wide,smoke_free_candidate=smoke_free,
    raw_flash_duration=float(a.flash_duration),raw_flash_alpha=float(a.flash_max_alpha)))
 output=pd.DataFrame(rows);output.to_csv(match/"damage-visibility-probes.csv",index=False)
 stats=dict(rows=len(output),static_clear_4_3=int(output.static_clear_4_3.sum()),static_clear_16_9=int(output.static_clear_16_9.sum()),smoke_free_candidate=int(output.smoke_free_candidate.sum()),mesh_sha256=hashlib.sha256(mesh.read_bytes()).hexdigest(),geometry_patch=mappatch,
  status="unvalidated_candidates",limitations=["Damage events include wallbangs; not a ground-truth visibility test.","Static map + approximate body/eye offsets + smoke spheres; dynamic doors, smoke holes and exact hitboxes not modeled.","Aspect ratio is evaluated as alternatives; player viewport not inferred.","Flash fields are raw and are not interpreted as perception onset."])
 (match/"visibility-probe-summary.json").write_text(json.dumps(stats,indent=2),encoding="utf-8");print(match.name,stats,flush=True)
if __name__=="__main__":
 ap=argparse.ArgumentParser();ap.add_argument("matches",type=Path);ap.add_argument("--assets",type=Path,required=True);a=ap.parse_args()
 for match in sorted(a.matches.iterdir()):
  if (match/"manifest.json").exists():probe(match,a.assets)
