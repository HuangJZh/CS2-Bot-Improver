"""Compare paired, fixed-destination loopback jump observations.

Success requires stable grounded arrival at the target area in the fixture.
These are controlled navigation trials, not natural-match success rates.
"""
import argparse
import json
from pathlib import Path


def read_run(path):
    rows = [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]
    starts = [i for i, row in enumerate(rows) if row["kind"] == "load"]
    if starts:
        rows = rows[starts[-1]:]
    if not any(row["kind"] == "complete" for row in rows):
        raise ValueError(f"Incomplete run: {path}")
    trials = {}
    for row in rows:
        if row["kind"] == "trial_start":
            if row.get("freeze") is not False or row.get("warmup") is not False or row.get("seconds_after_freeze", -1) < 4.25:
                raise ValueError(f"Trial lacks a verified live-round start outside the opening window: {path}")
            key = (row["map"], row["Name"], row["variant"])
            if key in trials:
                raise ValueError(f"Duplicate trial: {key}")
            trials[key] = {"start": row}
        if row["kind"] == "result":
            key = (row["map"], row["Name"], row["variant"])
            if key not in trials or "result" in trials[key]:
                raise ValueError(f"Unpaired result: {key}")
            trials[key]["result"] = row
    if any("result" not in trial for trial in trials.values()):
        raise ValueError("Missing trial result")
    return trials


def smooth(result):
    return result["success"] and result["seconds"] <= 2.5 and result["jumps"] <= 1


def compare(baseline, candidate):
    if baseline.keys() != candidate.keys():
        raise ValueError("The compared runs use different cases or initial-angle variants")
    records = []
    for key, old in baseline.items():
        new = candidate[key]
        if any(old["start"][field] != new["start"][field] for field in ("start", "end", "From", "To")):
            raise ValueError(f"Changed navigation goal: {key}")
        a, b = old["result"], new["result"]
        records.append({"map": key[0], "case": key[1], "variant": key[2],
                        "from_area": old["start"]["From"], "to_area": old["start"]["To"],
                        "start": old["start"]["start"], "landing": old["start"]["end"],
                        "baseline": a, "candidate": b})
    maps = {}
    for name in sorted({key[0] for key in baseline}):
        rows = [r for r in records if r["map"] == name]
        maps[name] = {"trials": len(rows),
                      "baseline_pass": sum(r["baseline"]["success"] for r in rows),
                      "candidate_pass": sum(r["candidate"]["success"] for r in rows),
                      "baseline_smooth": sum(smooth(r["baseline"]) for r in rows),
                      "candidate_smooth": sum(smooth(r["candidate"]) for r in rows)}
    return {"trials": len(records), "cases": len({key[:2] for key in baseline}), "round_live_verified": True,
            "maps": maps, "records": records,
            "limits": "Fixed native destination, standing starts, two initial-angle variants, 8-second deadline; not a natural-match success-rate estimate."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", required=True, type=Path)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--out", required=True, type=Path)
    args = parser.parse_args()
    report = compare(read_run(args.baseline), read_run(args.candidate))
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in report.items() if key != "records"}, ensure_ascii=False, indent=2))
