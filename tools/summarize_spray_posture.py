"""Descriptive posture in AK/M4 shot sequences; not tactical intent or population rates."""
import argparse
import json
from pathlib import Path

import pandas as pd


def summarize(root):
    rows = []
    for match in sorted(root.iterdir()):
        source = match / "shot-state-comparison.parquet"
        if not source.exists():
            continue
        shots = pd.read_parquet(source)
        shots = shots[shots.item_def_index.isin([7, 16, 60])
                      & ~shots.is_warmup_period.fillna(True)
                      & ~shots.is_freeze_period.fillna(True)].copy()
        shots = shots.sort_values(["steamid", "round", "tick"])
        # 0.5 seconds on these 64-tick demos. A sequence is not necessarily
        # a single-target fight or an uninterrupted trigger hold.
        groups = shots.groupby(["steamid", "round", "item_def_index"], sort=False)
        for (player, round_no, weapon), group in groups:
            for _, burst in group.groupby(group.tick.diff().gt(32).cumsum()):
                if len(burst) < 5:
                    continue
                crouch = burst.duck_amount
                rows.append({"match": match.name, "steamid": str(player),
                             "name": burst.user_name.iloc[0], "round": int(round_no),
                             "weapon": int(weapon), "start_tick": int(burst.tick.iloc[0]),
                             "shots": len(burst), "posture_missing": bool(crouch.isna().any()),
                             "any_full_crouch": bool(crouch.ge(.9).any()),
                             "all_full_crouch": bool(crouch.ge(.9).all()),
                             "standing_start_to_crouch": bool(crouch.iloc[0] <= .1 and crouch.ge(.9).any())})
    return pd.DataFrame(rows)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("matches", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = summarize(args.matches)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    result.to_csv(args.output, index=False)
    for name, group in result.groupby("name"):
        valid = group[~group.posture_missing]
        print(json.dumps({"name": name, "sequences": len(valid),
                          "any_crouch": int(valid.any_full_crouch.sum()),
                          "standing_to_crouch": int(valid.standing_start_to_crouch.sum())}))
