"""Join shot callbacks to demo game_time, preserving native-state timing limits."""
import argparse
import json
from pathlib import Path

import pandas as pd


def compare(motion_path, shots_path):
    motion = json.loads(motion_path.read_text(encoding="utf-8"))
    shots = pd.read_parquet(shots_path)
    shots = shots[shots.item_def_index.eq(7)].copy()
    fires = [c for c in motion["clocks"] if c["kind"] == "weapon_fire_bracket" and c["weapon"] == "weapon_ak47"]
    rows, used = [], set()
    for fire in fires:
        # Demo-relative ticks differ from runtime ticks. Match by recorded
        # game time only; multiple possible shots are an error, not a choice.
        candidates = shots[(shots.game_time - fire["time"]).abs().le(.03)]
        if len(candidates) != 1:
            raise ValueError("shot time missing or ambiguous")
        shot = candidates.iloc[0]
        key = (int(shot.tick), str(shot.user_steamid))
        if key in used:
            raise ValueError("shot reused")
        used.add(key)
        index = fire["count"]
        if not 0 <= index < len(motion["ticks"]):
            raise ValueError("callback bracket outside motion data")
        tick = motion["ticks"][index]
        command = motion["commands"][index]
        rows.append({"callback_time": fire["time"], "demo_time": float(shot.game_time),
                     "demo_tick": int(shot.tick), "native_record_index_bracket": index,
                     "recoil_index": float(shot.recoil_index), "command_view_present": bool(command["Fields"] & 8),
                     "command_pitch": command["Pitch"] if command["Fields"] & 8 else None,
                     "pre_pitch": tick["Pre"]["Pitch"], "post_pitch": tick["Post"]["Pitch"],
                     "shot_pitch": float(shot.angles_x),
                     "shot_minus_post_pitch": ((float(shot.angles_x) - tick["Post"]["Pitch"] + 180) % 360) - 180,
                     "post_yaw": tick["Post"]["Yaw"], "shot_yaw": float(shot.angles_y),
                     "shot_minus_post_yaw": ((float(shot.angles_y) - tick["Post"]["Yaw"] + 180) % 360) - 180})
    if not rows:
        raise ValueError("no matching AK shot callbacks")
    return pd.DataFrame(rows)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("motion", type=Path)
    parser.add_argument("shots", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = compare(args.motion, args.shots)
    result.to_csv(args.output, index=False)
    print(json.dumps({"joined": len(result), "max_time_difference": float((result.demo_time-result.callback_time).abs().max()),
                      "max_abs_shot_minus_post_pitch": float(result.shot_minus_post_pitch.abs().max()),
                      "limits": "Native record index is a callback count bracket, not a proven shot-subtick state. Difference is not human motor error; only stable-view fixtures isolate the recoil contribution."}, indent=2))
