"""Summarize event-callback/tick telemetry; no claim of exact subtick timing."""
import collections
import json
import math
import pathlib
import statistics
import sys


def dist(a, b):
    if any(v is None for v in (*a, *b)):
        return None
    return math.hypot(*[((x-y+180) % 360)-180 for x, y in zip(a, b)])


def summary(values):
    values = sorted(v for v in values if v is not None and math.isfinite(v))
    if not values:
        return {"n": 0}
    return {"n": len(values), "median": statistics.median(values),
            "p90": values[min(len(values)-1, int(len(values)*.9))],
            "mean": statistics.mean(values)}


def main(directory):
    counts = collections.Counter()
    gates = collections.Counter()
    phases = collections.Counter()
    sample_fail = collections.Counter()
    metrics = collections.defaultdict(list)
    contacts = []
    early_shots = []
    switches = 0
    sessions = []
    for path in sorted(directory.glob("aim-*.jsonl")):
        live = {}
        prior = {}
        early_in_file = []
        with path.open(encoding="utf-8-sig") as stream:
            for line in stream:
                try:
                    row = json.loads(line)
                except json.JSONDecodeError:
                    continue
                kind = row.get("kind")
                if kind == "session":
                    sessions.append(row)
                if row.get("game_freeze") or row.get("game_warmup") or not row.get("alive"):
                    continue
                if kind not in ("server_tick_observation", "weapon_fire_callback", "player_hurt_callback", "player_kill_callback"):
                    continue
                counts[kind] += 1
                now = row["time"]
                slot = row["slot"]
                key = (row["pawn"], row.get("enemy"))
                for early in early_in_file:
                    if early["slot"] == slot and early["pawn"] == row["pawn"] and 0 < now-early["time"] < .3:
                        if kind == "server_tick_observation" and row.get("native_guard_hold"):
                            early["later_hold"] = True
                if kind == "weapon_fire_callback" and row.get("native_guard_reason") == "no-observation":
                    early = dict(slot=slot, pawn=row["pawn"], time=now, later_hold=False,
                                 marked_first=row.get("spray_shots") == 1)
                    early_in_file.append(early)
                    early_shots.append(early)
                visible = (row.get("native_visible") and row.get("sensor_visible")
                           and row.get("enemy") not in (None, 4294967295)
                           and row.get("native_guard_reason") not in ("invalid-enemy", "target-changed"))
                if visible:
                    contact = live.get(slot)
                    new_contact = contact is None or contact["key"] != key or now-contact["last"] > .25
                    if new_contact and kind == "server_tick_observation":
                        if contact is not None and contact["key"] != key and now-contact["last"] < .25:
                            switches += 1
                        contact = dict(key=key, slot=slot, start=now, last=now, shot=None,
                                       first_allow=None, tick_count=0, gap=row.get("contact_gap_degrees"),
                                       gates=collections.Counter(), model_gap=[], actual_gap=[])
                        live[slot] = contact
                        contacts.append(contact)
                    elif new_contact:
                        contact = None
                    if contact is not None:
                        contact["last"] = now
                    if contact is not None and row.get("effective_allow_fire") and contact["first_allow"] is None:
                        contact["first_allow"] = now
                    if contact is not None and kind == "weapon_fire_callback" and contact["shot"] is None:
                        contact["shot"] = now
                else:
                    contact = None
                has_plan = row.get("model_plan_start_time") is not None and row.get("phase") not in (None, "perception")
                actual_gap = dist((row.get("pitch"), row.get("yaw")),
                                  (row.get("planned_pitch"), row.get("planned_yaw")))
                bearing_gap = (dist((row.get("pitch"), row.get("yaw")),
                                    (row.get("believed_target_pitch"), row.get("believed_target_yaw"))) if has_plan else None)
                model_gap = (dist((row.get("planned_pitch"), row.get("planned_yaw")),
                                  (row.get("believed_target_pitch"), row.get("believed_target_yaw"))) if has_plan else None)
                if kind == "server_tick_observation":
                    if row.get("native_visible"):
                        sample_fail[str(row.get("sample_result"))] += 1
                    if visible:
                        gates[str(row.get("native_guard_reason"))] += 1
                        phases[str(row.get("phase"))] += 1
                        metrics["visible_actual_vs_model_deg"].append(actual_gap)
                        metrics["visible_actual_vs_belief_deg"].append(bearing_gap)
                        metrics["visible_model_vs_belief_deg"].append(model_gap)
                        if row.get("allow_fire"):
                            metrics["model_allows_actual_gap_deg"].append(actual_gap)
                        if row.get("effective_allow_fire"):
                            metrics["released_actual_vs_belief_deg"].append(bearing_gap)
                        if contact:
                            contact["tick_count"] += 1
                            contact["gates"][str(row.get("native_guard_reason"))] += 1
                    last = prior.get(slot)
                    if last and key == (last.get("pawn"), last.get("enemy")):
                        old_start, start = last.get("model_plan_start_time"), row.get("model_plan_start_time")
                        if old_start is not None and start is not None and old_start != start and now-last["time"] < .1:
                            counts["same_target_plan_restart"] += 1
                    prior[slot] = row
                elif kind == "weapon_fire_callback":
                    metrics["shot_actual_vs_model_deg"].append(actual_gap)
                    metrics["shot_actual_vs_belief_deg"].append(bearing_gap)
                    metrics["shot_speed_units_s"].append(math.hypot(row.get("vx", 0), row.get("vy", 0)))
                    counts["shot_gate:"+str(row.get("native_guard_reason"))] += 1
                    if row.get("spray_shots") == 1:
                        metrics["first_shot_speed_units_s"].append(math.hypot(row.get("vx", 0), row.get("vy", 0)))
                        metrics["first_shot_actual_vs_belief_deg"].append(bearing_gap)
                elif kind == "player_hurt_callback":
                    counts["hitgroup:"+str(row.get("event_hitgroup"))] += 1
                elif kind == "player_kill_callback":
                    counts["headshot_kills"] += bool(row.get("event_headshot"))
    sustained = [c for c in contacts if c["last"]-c["start"] >= .25]
    for c in sustained:
        if c["shot"] is not None:
            metrics["sustained_contact_to_first_shot_ms"].append(1000*(c["shot"]-c["start"]))
        if c["first_allow"] is not None:
            metrics["sustained_contact_to_release_ms"].append(1000*(c["first_allow"]-c["start"]))
            if c["shot"] is not None and c["shot"] >= c["first_allow"]:
                metrics["release_to_first_shot_ms"].append(1000*(c["shot"]-c["first_allow"]))
    result = dict(sessions=[{k:s.get(k) for k in ("map", "difficulty", "module_version", "model_active")} for s in sessions],
                  counts=counts, visible_tick_gates=gates, visible_tick_phases=phases,
                  native_visible_sample_results=sample_fail, metrics={k:summary(v) for k,v in metrics.items()},
                  continuous_target_switches=switches, sustained_contacts=len(sustained),
                  perception_phase_shots=len(early_shots),
                  perception_shots_marked_first=sum(e["marked_first"] for e in early_shots),
                  perception_shots_followed_by_hold_within_300ms=sum(e["later_hold"] for e in early_shots),
                  sustained_without_shot=sum(c["shot"] is None for c in sustained),
                  longest_unshot_contacts=sorted([dict(slot=c["slot"],duration=c["last"]-c["start"],gates=c["gates"])
                         for c in sustained if c["shot"] is None], key=lambda c:-c["duration"])[:10],
                  limitations="Bot-vs-bot natural sample, no paired counterfactual, tick/callback timing only; capture starts and sensor flicker truncate contacts. Shot speed is post-callback and may already reflect post-shot stopping. Eye angles do not equal recoil-adjusted bullet direction.")
    (directory/"combat-summary.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main(pathlib.Path(sys.argv[1]))
