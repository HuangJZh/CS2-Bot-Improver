"""Offline descriptive aim analysis. Does not estimate visibility or reaction time."""
from pathlib import Path
import argparse,json
import numpy as np
import pandas as pd
from demoparser2 import DemoParser

def wrap(a):return (a+180)%360-180
def group(w):
 w=w.removeprefix("weapon_")
 if w in {"awp","ssg08","scar20","g3sg1"}:return "sniper"
 if w in {"ak47","m4a1","m4a1_silencer","aug","sg556","galilar","famas"}:return "rifle"
 if w in {"glock","hkp2000","usp_silencer","p250","tec9","fiveseven","cz75a","elite","deagle","revolver"}:return "pistol"
 if w in {"mac10","mp9","mp7","mp5sd","ump45","p90","bizon"}:return "smg"
 return "other"
def analyze(path,out,steam):
 parser=DemoParser(str(path));header=parser.parse_header()
 fields=["pitch","yaw","X","Y","Z","is_alive","active_weapon_name","is_scoped","game_time","total_rounds_played","is_warmup_period","is_freeze_period"]
 frames=parser.parse_ticks(fields,players=[steam]).sort_values("tick").drop_duplicates("tick")
 frames=frames.dropna(subset=["pitch","yaw","game_time"])
 frames.to_parquet(out/(path.stem+"-views.parquet"),index=False)
 time_per_tick=float(np.median(np.diff(frames.game_time)/np.diff(frames.tick)))
 assert .005<time_per_tick<.04,time_per_tick
 fires=parser.parse_event("weapon_fire")
 fires=fires[fires.user_steamid.astype(str)==str(steam)].copy()
 fires["category"]=fires.weapon.map(group);fires=fires[fires.category!="other"].sort_values("tick")
 hurts=parser.parse_event("player_hurt");hurts=hurts[hurts.attacker_steamid.astype(str)==str(steam)]
 deaths=parser.parse_event("player_death")
 deaths=deaths[deaths.attacker_steamid.astype(str)==str(steam)]
 by_tick=frames.set_index("tick");rows=[];last=-1e9;last_weapon=None;last_round=-1;burst=0
 for shot in fires.itertuples():
  idx=np.searchsorted(frames.tick.to_numpy(),shot.tick,side="right")-1
  if idx<0:continue
  current=frames.iloc[idx]
  if shot.tick-current.tick>2 or current.is_warmup_period or current.is_freeze_period or not current.is_alive:continue
  first=(shot.tick-last)*time_per_tick>.30 or shot.weapon!=last_weapon or current.total_rounds_played!=last_round
  if first:burst+=1
  gap=(shot.tick-last)*time_per_tick if last_weapon==shot.weapon and current.total_rounds_played==last_round else None
  # Use only ticks BEFORE the shot; the event tick may already contain recoil.
  window=frames[(frames.tick<shot.tick)&(frames.tick>=shot.tick-round(.160/time_per_tick))]
  valid=len(window)>=6 and np.diff(window.tick).max()<=2 and window.is_alive.all() and (window.total_rounds_played==current.total_rounds_played).all()
  if valid:
   angles=window[["pitch","yaw"]].to_numpy();d=wrap(np.diff(angles,axis=0));dt=np.diff(window.tick)*time_per_tick
   steps=np.linalg.norm(d,axis=1);travel=float(steps.sum());net=float(np.linalg.norm(wrap(angles[-1]-angles[0])))
   peak=float(np.max(steps/dt));recent=float(steps[-1]/dt[-1])
  else:travel=net=peak=recent=None
  rows.append(dict(map=header["map_name"],tick=int(shot.tick),round=int(current.total_rounds_played)+1,weapon=shot.weapon,category=shot.category,
   scoped=bool(current.is_scoped),burst_start=bool(first),burst=burst,interval_ms=gap*1000 if gap is not None else None,
   preshot160_travel_deg=travel,preshot160_net_deg=net,preshot160_peak_deg_s=peak,preshot_last_step_deg_s=recent))
  last=shot.tick;last_weapon=shot.weapon;last_round=current.total_rounds_played
 result=pd.DataFrame(rows);result.to_csv(out/(path.stem+"-shots.csv"),index=False)
 fires.to_csv(out/(path.stem+"-fire-events.csv"),index=False);hurts.to_csv(out/(path.stem+"-hurt-events.csv"),index=False)
 summary=dict(file=path.name,map=header["map_name"],patch=header.get("patch_version"),tick_interval_seconds=time_per_tick,shots=len(result),raw_event_kills=len(deaths),raw_event_damage=int(hurts.dmg_health.sum()))
 print(json.dumps(summary,ensure_ascii=False),flush=True)
 return result,summary

def main():
 ap=argparse.ArgumentParser();ap.add_argument("folder",type=Path);ap.add_argument("--out",type=Path,required=True);ap.add_argument("--steam",type=int,default=76561198074762801);a=ap.parse_args();a.out.mkdir(parents=True,exist_ok=True)
 all_rows=[];maps=[]
 for f in sorted(a.folder.glob("*.dem")):
  r,s=analyze(f,a.out,a.steam);all_rows.append(r);maps.append(s)
 df=pd.concat(all_rows,ignore_index=True);summary=[]
 for category,g in df.groupby("category"):
  first=g[g.burst_start].dropna(subset=["preshot160_travel_deg"])
  summary.append(dict(category=category,shots=len(g),bursts=len(first),pre160_travel_p25=float(first.preshot160_travel_deg.quantile(.25)),pre160_travel_median=float(first.preshot160_travel_deg.median()),pre160_travel_p75=float(first.preshot160_travel_deg.quantile(.75)),pre160_under_halfdegree_pct=float((first.preshot160_travel_deg<.5).mean()*100),pre160_over5degree_pct=float((first.preshot160_travel_deg>5).mean()*100),pre160_peak_median=float(first.preshot160_peak_deg_s.median())))
 weapons=[dict(weapon=w,samples=len(g),median_pre160_travel_deg=float(g.preshot160_travel_deg.median())) for w,g in df[df.burst_start].dropna(subset=["preshot160_travel_deg"]).groupby("weapon")]
 output=dict(player_steamid=str(a.steam),maps=maps,groups=summary,weapons=weapons,limitations=["Not reaction time or time-to-damage: target visibility not reconstructed.","First-in-burst defined as >300ms gap or weapon/round change; not necessarily first shot of a duel.","Preshot window excludes firing tick to reduce recoil contamination. Prior shots may still affect it.","Recorded view is server demo state, not raw mouse input. One BO3 is not a population benchmark."])
 (a.out/"summary.json").write_text(json.dumps(output,indent=2,ensure_ascii=False),encoding="utf-8")
 print(json.dumps(summary,indent=2),flush=True)
if __name__=="__main__":main()
