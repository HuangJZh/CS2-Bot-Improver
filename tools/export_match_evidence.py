"""Preserve full demo evidence. Visibility and encounters are later derived stages."""
import argparse,hashlib,json,importlib.metadata
from pathlib import Path
import numpy as np
import pandas as pd
from demoparser2 import DemoParser
EVENTS=["weapon_fire","fire_bullets","player_hurt","player_death","round_start","round_freeze_end","round_end","round_announce_match_start","smokegrenade_detonate","smokegrenade_expired","flashbang_detonate","hegrenade_detonate","inferno_startburn","inferno_expire","player_sound","bomb_planted","bomb_begindefuse","bomb_defused"]
FIELDS=["X","Y","Z","pitch","yaw","duck_amount","fov","team_num","is_alive","is_scoped","active_weapon_name","game_time","total_rounds_played","is_warmup_period","is_freeze_period","flash_duration","flash_max_alpha"]
def digest(path):
 h=hashlib.sha256()
 with path.open("rb") as f:
  while b:=f.read(8*1024*1024):h.update(b)
 return h.hexdigest()
def export(path,output):
 target=output/path.stem;target.mkdir(parents=True,exist_ok=True)
 parser=DemoParser(str(path));header=parser.parse_header();players=parser.parse_player_info()
 players.to_parquet(target/"players.parquet",index=False)
 frames=parser.parse_ticks(FIELDS).sort_values(["tick","steamid"]).drop_duplicates(["tick","steamid"])
 assert not frames.duplicated(["tick","steamid"]).any()
 clock=frames[["tick","game_time","total_rounds_played","is_warmup_period","is_freeze_period"]].drop_duplicates("tick").sort_values("tick")
 valid=clock.dropna(subset=["game_time"]);dt=np.diff(valid.game_time)/np.diff(valid.tick);interval=float(np.median(dt))
 assert .005<interval<.04,interval
 frames.to_parquet(target/"frames.parquet",index=False);clock.to_parquet(target/"clock.parquet",index=False)
 supported=set(parser.list_game_events());counts={};missing=[];derived=[]
 for event in EVENTS:
  if event not in supported and event not in {"round_start","round_end"}:missing.append(event);continue
  if event not in supported:derived.append(event)
  data=parser.parse_event(event)
  if not isinstance(data,pd.DataFrame):missing.append(event);continue
  data=data.copy();data["event"]=event
  # Keep events without a clock match; don't manufacture timestamps.
  data=data.merge(clock,on="tick",how="left",validate="many_to_one")
  data["clock_missing"]=data.game_time.isna()
  data.to_parquet(target/(event+".parquet"),index=False);counts[event]=len(data)
 manifest=dict(schema="aim-evidence-v1",source=str(path.resolve()),source_sha256=digest(path),header=header,parser=importlib.metadata.version("demoparser2"),tick_interval_seconds=interval,players=len(players),frame_rows=len(frames),events=counts,unavailable_events=missing,derived_event_types=derived,
  semantics={"positions":"recorded pawn origin, not eye or hitbox positions","angles":"recorded pitch/yaw, not raw mouse input","visibility":"not inferred; spotted is deliberately unused","phase":"recorded warmup/freeze fields; no implicit deletion of raw events","flash":"raw network fields; not yet calibrated as full-blind state"})
 starts=parser.parse_event("round_announce_match_start")
 if isinstance(starts,pd.DataFrame) and not starts.empty:
  zero=starts.merge(clock[["tick","total_rounds_played"]],on="tick",how="left")
  selected=zero.loc[zero.total_rounds_played==0,"tick"]
  if not selected.empty:manifest["competitive_start_candidate_tick"]=int(selected.max())
 manifest["phase_note"]="Latest zero-round match-start marker; raw events preserved. Replays still needed to audit restarts."
 (target/"manifest.json").write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding="utf-8")
 print(json.dumps({"map":header["map_name"],"players":len(players),"frame_rows":len(frames),"events":counts},ensure_ascii=False),flush=True)
def main():
 p=argparse.ArgumentParser();p.add_argument("folder",type=Path);p.add_argument("--out",type=Path,required=True);a=p.parse_args()
 for demo in sorted(a.folder.glob("*.dem")):export(demo,a.out)
if __name__=="__main__":main()
