"""Compare demo shot-event fields to same-tick network view; no raw-input claim."""
from pathlib import Path
import argparse,json
import numpy as np
import pandas as pd

def compare(match):
 m=json.loads((match/"manifest.json").read_text());shots=pd.read_parquet(match/"fire_bullets.parquet")
 shots=shots[(shots.tick>=m["competitive_start_candidate_tick"]) & ~shots.is_warmup_period.fillna(True)].copy()
 frames=pd.read_parquet(match/"frames.parquet",filters=[("tick","in",shots.tick.unique().tolist())],columns=["tick","steamid","pitch","yaw","X","Y","Z","duck_amount","is_alive","active_weapon_name","is_scoped","fov"])
 shots["steamid"]=shots.user_steamid.astype('uint64')
 merged=shots.merge(frames,on=["tick","steamid"],how="left",validate="many_to_one")
 merged["event_minus_view_pitch"]=((merged.angles_x-merged.pitch+180)%360)-180
 merged["event_minus_view_yaw"]=((merged.angles_y-merged.yaw+180)%360)-180
 merged["event_origin_height"]=merged.origin_z-merged.Z
 merged["approx_eye_height"]=64-18*merged.duck_amount
 merged["event_minus_approx_eye_height"]=merged.event_origin_height-merged.approx_eye_height
 merged["view_angle_missing"]=merged.pitch.isna()|merged.yaw.isna()
 merged.to_parquet(match/"shot-state-comparison.parquet",index=False)
 summary={"shots":len(merged),"missing_views":int(merged.view_angle_missing.sum()),"event_view_pitch_abs_median":float(merged.event_minus_view_pitch.abs().median()),"event_view_yaw_abs_median":float(merged.event_minus_view_yaw.abs().median()),"eye_height_error_abs_median":float(merged.event_minus_approx_eye_height.abs().median()),"eye_height_error_abs_p95":float(merged.event_minus_approx_eye_height.abs().quantile(.95)),"semantics":"Recorded shot-event angles/origin vs same-tick recorded pawn state. Includes recoil/timing differences; not mouse input or exact eye geometry truth."}
 (match/"shot-state-summary.json").write_text(json.dumps(summary,indent=2),encoding="utf-8");print(match.name,summary,flush=True)
if __name__=="__main__":
 p=argparse.ArgumentParser();p.add_argument("matches",type=Path);a=p.parse_args()
 for m in sorted(a.matches.iterdir()):
  if (m/"manifest.json").exists():compare(m)
