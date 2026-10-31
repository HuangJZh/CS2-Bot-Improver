import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from audit_gate_trial import audit


class GateTrialAudit(unittest.TestCase):
    def sample(self):
        runtime = [{"schema": "bot-aim-telemetry-v1", "model_active": True},
                   {"kind": "test_gate_control", "action": "block", "time": 1., "until": 4.},
                   {"kind": "server_tick_observation", "time": 2., "slot": 2, "pawn": 42,
                    "weapon": "weapon_ak47", "test_gate_active": True, "model_fire_suppression_owned": True},
                   {"kind": "weapon_fire_callback", "time": 4.5, "slot": 2, "pawn": 42, "event_weapon": "weapon_ak47"}]
        motion = {"schema": "probe-native-motion-v1", "slot": 2, "pawn": 42, "subs": [],
                  "commands": [{"Fields": 16, "Buttons": n} for n in [0, 1, 1]],
                  "ticks": [{"NumSubtick": 0, "Post": {"Buttons": n}} for n in [0, 0, 1]],
                  "clocks": [{"kind": "tick_count_bracket", "time": t, "count": i+1} for i,t in enumerate([.5, 2., 5.])]}
        return runtime, motion

    def check_sample(self, runtime, motion):
        with tempfile.TemporaryDirectory() as directory:
            r, m = Path(directory)/"runtime.jsonl", Path(directory)/"native.json"
            r.write_text("\n".join(json.dumps(row) for row in runtime), encoding="utf-8")
            m.write_text(json.dumps(motion), encoding="utf-8")
            return audit(r, m, "ak47")

    def test_controlled_request_clear_recovery(self):
        result = self.check_sample(*self.sample())
        self.assertEqual(result["status"], "steady-gate-control-observed")

    def test_no_requests_is_not_acceptance(self):
        runtime, motion = self.sample()
        motion["commands"][1]["Buttons"] = 0
        self.assertEqual(self.check_sample(runtime, motion)["status"], "insufficient-input")

    def test_actual_fire_inside_block_reported(self):
        runtime, motion = self.sample()
        event = copy.deepcopy(runtime[-1]); event["time"] = 2.
        runtime.append(event)
        self.assertEqual(self.check_sample(runtime, motion)["status"], "fire-during-steady-block")

    def test_missing_recovery_not_accepted(self):
        runtime, motion = self.sample()
        runtime.pop()
        self.assertEqual(self.check_sample(runtime, motion)["status"], "no-recovery-fire")

    def test_boundary_fire_is_retained_not_hidden_by_steady_window(self):
        runtime, motion = self.sample()
        event = copy.deepcopy(runtime[-1]); event["time"] = 1.02
        runtime.append(event)
        result = self.check_sample(runtime, motion)
        self.assertEqual(result["status"], "steady-gate-control-observed")
        self.assertEqual(result["gun_fire_callbacks_in_full_block"], 1)
        self.assertEqual(result["gun_fire_callbacks_in_steady_window"], 0)

    def test_pawn_reuse_not_same_trial(self):
        runtime, motion = self.sample()
        runtime[2]["pawn"] = 99
        self.assertEqual(self.check_sample(runtime, motion)["status"], "condition-not-maintained")

    def test_model_disabled_during_window_not_accepted(self):
        runtime, motion = self.sample()
        runtime.append({"kind": "model_control", "time": 2.5, "active": False})
        self.assertEqual(self.check_sample(runtime, motion)["status"], "condition-not-maintained")

    def test_model_activation_event_overrides_initial_header(self):
        runtime, motion = self.sample()
        runtime[0]["model_active"] = False
        runtime.append({"kind": "model_control", "time": .8, "active": True})
        self.assertEqual(self.check_sample(runtime, motion)["status"], "steady-gate-control-observed")

    def test_native_recording_started_too_late_rejected(self):
        runtime, motion = self.sample()
        motion["clocks"][0]["time"] = 1.1
        with self.assertRaises(ValueError):
            self.check_sample(runtime, motion)


if __name__ == "__main__":
    unittest.main()
