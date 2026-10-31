"""Validate full AK posture trials before describing recorded weapon inaccuracy.

Not a measurement of human recoil control, hit rate, or tactical benefit.
"""
import argparse
import hashlib
import json
from pathlib import Path

import pandas as pd


def validate(path, crouch):
    frame = pd.read_parquet(path)
    frame = frame[frame.item_def_index.eq(7)].sort_values("tick")
    errors = []
    if len(frame) != 30:
        errors.append("requires exactly 30 AK shots")
    if frame.tick.duplicated().any():
        errors.append("duplicate shot ticks")
    if frame.user_steamid.nunique() != 1:
        errors.append("requires one actor")
    if frame.recoil_index.tolist() != list(range(30)):
        errors.append("requires fresh recoil indices 0..29")
    if frame.duck_amount.isna().any() or not (
        frame.duck_amount.ge(.9).all() if crouch else frame.duck_amount.le(.1).all()
    ):
        errors.append("posture condition not maintained")
    if not frame.inaccuracy.notna().all():
        errors.append("missing inaccuracy")
    if errors:
        raise ValueError(f"{path}: {'; '.join(errors)}")
    return {"path": str(path.resolve()),
            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "shots": len(frame), "first_shot_inaccuracy": float(frame.inaccuracy.iloc[0]),
            "indices_5_to_20_median_inaccuracy": float(frame[frame.recoil_index.between(5, 20)].inaccuracy.median())}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("standing", type=Path)
    parser.add_argument("crouched", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    standing = validate(args.standing, False)
    crouched = validate(args.crouched, True)
    result = {"standing": standing, "crouched": crouched,
              "first_shot_inaccuracy_reduction": 1 - crouched["first_shot_inaccuracy"] / standing["first_shot_inaccuracy"],
              "late_inaccuracy_reduction": 1 - crouched["indices_5_to_20_median_inaccuracy"] / standing["indices_5_to_20_median_inaccuracy"],
              "limits": "Single scripted trial per stance; recorded weapon inaccuracy only. No causal estimate of human recoil control or rank. Ground and movement require separate telemetry checks; absent player_inair is not grounded evidence."}
    args.output.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))
