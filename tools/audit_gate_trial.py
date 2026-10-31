"""Audit a bounded, deliberately blocked fire-control fixture, not natural aim skill."""
import argparse
import json
from pathlib import Path

from audit_native_motion import audit as audit_motion


def audit(runtime_path, motion_path, weapon):
    audit_motion(motion_path)
    rows = [json.loads(line) for line in runtime_path.read_text(encoding="utf-8-sig").splitlines() if line.strip()]
    motion = json.loads(motion_path.read_text(encoding="utf-8"))
    if not rows or rows[0].get("schema") != "bot-aim-telemetry-v1":
        raise ValueError("missing runtime schema")
    blocks = [r for r in rows if r.get("kind") == "test_gate_control" and r.get("action") == "block"]
    if len(blocks) != 1:
        raise ValueError("requires exactly one timed block")
    start, end = blocks[0]["time"], blocks[0]["until"]
    if not .5 <= end-start <= 5.01:
        raise ValueError("invalid block duration")
    slot = motion["slot"]
    clock = [c for c in motion["clocks"] if c["kind"] == "tick_count_bracket"]
    if not clock or clock[0]["time"] >= start or clock[-1]["time"] <= end+.3:
        raise ValueError("native capture does not cover block and recovery")
    # A clock count brackets completed physics records; count-1 is the latest
    # completed record, not an exact shot-subtick mapping. Ignore edge windows.
    indices = {c["count"]-1 for c in clock if start+.1 <= c["time"] <= end-.05 and c["count"] > 0}
    commands = [motion["commands"][i] for i in sorted(indices)]
    ticks = [motion["ticks"][i] for i in sorted(indices)]
    samples = [r for r in rows if r.get("kind") == "server_tick_observation" and r.get("slot") == slot
               and start+.1 <= r["time"] <= end-.05]
    normalize = lambda value: str(value).removeprefix("weapon_")
    fires = [r for r in rows if r.get("kind") == "weapon_fire_callback" and r.get("slot") == slot
             and r.get("pawn") == motion["pawn"]
             and normalize(r.get("event_weapon")) == normalize(weapon)]
    blocked_fires = [r for r in fires if start+.1 <= r["time"] <= end-.05]
    full_block_fires = [r for r in fires if start <= r["time"] <= end]
    edge_fires = [r for r in full_block_fires if r not in blocked_fires]
    recovery = [r for r in fires if end+.1 <= r["time"] <= clock[-1]["time"]]
    requests = sum(bool(c["Buttons"] & 1) for c in commands if c["Fields"] & 16)
    post_attack = sum(bool(t["Post"]["Buttons"] & 1) for t in ticks)
    unknown_commands = sum(not bool(c["Fields"] & 16) for c in commands)
    controls = sorted((r for r in rows if r.get("kind") == "model_control"), key=lambda r:r["time"])
    model_active = rows[0].get("model_active") is True
    for control in controls:
        if control["time"] <= start+.1:
            model_active = control.get("active") is True
    mode_maintained = model_active and not any(start+.1 < r["time"] <= end-.05 and r.get("active") is not True for r in controls)
    condition = mode_maintained and bool(samples) and all(r.get("pawn") == motion["pawn"] and normalize(r.get("weapon")) == normalize(weapon)
                                    and r.get("test_gate_active") is True
                                    and r.get("model_fire_suppression_owned") is True for r in samples)
    status = ("condition-not-maintained" if not condition else "missing-input-fields" if unknown_commands
              else "fire-during-steady-block" if blocked_fires else "accepted-attack-during-block" if post_attack
              else "insufficient-input" if not requests else "no-recovery-fire" if not recovery
              else "steady-gate-control-observed")
    return {"runtime": str(runtime_path.resolve()), "motion": str(motion_path.resolve()), "slot": slot,
            "weapon": normalize(weapon), "block_start": start, "block_end": end,
            "status": status, "completed_native_records_in_steady_window": len(indices),
            "recorded_native_attack_requests": requests, "post_attack_records": post_attack,
            "gun_fire_callbacks_in_steady_window": len(blocked_fires), "recovery_gun_fire_callbacks": len(recovery),
            "gun_fire_callbacks_in_full_block": len(full_block_fires),
            "edge_fire_offsets_from_block_start_ms": [(r["time"]-start)*1000 for r in edge_fires],
            "limits": "Controlled output override, no Attack injection per protocol. One retained command per physics boundary can omit other commands. Completed-record clock brackets and callback times are not exact shot-subtick states. This does not validate natural decision timing, aim, or rank."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("runtime", type=Path)
    parser.add_argument("motion", type=Path)
    parser.add_argument("weapon")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    result = audit(args.runtime, args.motion, args.weapon)
    args.output.write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))
