"""Audit native motion buffers without treating missing commands as zero input."""
import argparse
import collections
import hashlib
import json
from pathlib import Path


def audit(path):
    data = json.loads(path.read_text(encoding="utf-8"))
    if data.get("schema") != "probe-native-motion-v1":
        raise ValueError("unsupported schema")
    ticks, commands, subs = data["ticks"], data["commands"], data["subs"]
    if len(ticks) != len(commands):
        raise ValueError("command/tick alignment lost")
    if sum(t["NumSubtick"] for t in ticks) != len(subs):
        raise ValueError("subtick count mismatch")
    valid = [c for c in commands if c["Fields"] & 8]
    clocks = [c for c in data["clocks"] if c["kind"] == "tick_count_bracket"]
    if any(c["count"] < 0 or c["count"] > len(ticks) for c in clocks):
        raise ValueError("native count outside buffer")
    if any(b["count"] < a["count"] or b["time"] <= a["time"] for a, b in zip(clocks, clocks[1:])):
        raise ValueError("non-monotonic clock")
    fire = [c for c in data["clocks"] if c["kind"] == "weapon_fire_bracket"]
    # Recorder hooks run before test injections/suppression/movement overrides.
    # Therefore command Attack and simulated buttons need not agree.
    return {"source": str(path.resolve()), "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
            "ticks": len(ticks), "commands": len(commands), "subticks": len(subs),
            "view_present": len(valid), "command_fields": dict(collections.Counter(c["Fields"] for c in commands)),
            "command_attack": sum(bool(c["Fields"] & 16 and c["Buttons"] & 1) for c in commands),
            "post_attack": sum(bool(t["Post"]["Buttons"] & 1) for t in ticks),
            "weapon_fire_callbacks": len(fire), "weapon_fire_counts": [c["count"] for c in fire],
            "view_pitch_min": min((c["Pitch"] for c in valid), default=None),
            "view_pitch_max": max((c["Pitch"] for c in valid), default=None),
            "limits": "Native input capture precedes injection/suppression. Fields bits distinguish absent data. Count brackets do not uniquely locate shot subticks; one command per native physics boundary may omit additional commands. No raw human mouse-input claim."}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    result = audit(args.source)
    content = json.dumps(result, indent=2)
    if args.output:
        args.output.write_text(content, encoding="utf-8")
    print(content)
