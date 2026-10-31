import json
from pathlib import Path
import sys
import tempfile
import unittest
import pandas as pd

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from audit_native_motion import audit
from compare_native_shots import compare


class NativeMotionAudit(unittest.TestCase):
    def sample(self):
        return {"schema": "probe-native-motion-v1",
                "ticks": [{"NumSubtick": 0, "Post": {"Buttons": 1}}],
                "commands": [{"Fields": 24, "Buttons": 0, "Pitch": 0, "Yaw": 0}],
                "subs": [], "clocks": [{"kind": "tick_count_bracket", "time": 1, "count": 0},
                                        {"kind": "weapon_fire_bracket", "count": 0}]}

    def run_audit(self, data):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "native.json"
            path.write_text(json.dumps(data), encoding="utf-8")
            return audit(path)

    def test_injected_fire_is_not_original_attack(self):
        result = self.run_audit(self.sample())
        self.assertEqual((result["command_attack"], result["post_attack"], result["weapon_fire_callbacks"]), (0, 1, 1))

    def test_absent_view_is_not_zero_angle(self):
        data = self.sample()
        data["commands"][0]["Fields"] = 0
        result = self.run_audit(data)
        self.assertEqual(result["view_present"], 0)
        self.assertIsNone(result["view_pitch_min"])

    def test_alignment_loss_rejected(self):
        data = self.sample()
        data["commands"] = []
        with self.assertRaises(ValueError):
            self.run_audit(data)

    def test_missing_subtick_data_rejected(self):
        data = self.sample()
        data["ticks"][0]["NumSubtick"] = 1
        with self.assertRaises(ValueError):
            self.run_audit(data)

    def test_backward_clock_rejected(self):
        data = self.sample()
        data["clocks"].append({"kind": "tick_count_bracket", "time": .5, "count": 1})
        with self.assertRaises(ValueError):
            self.run_audit(data)

    def test_ambiguous_shot_time_rejected(self):
        data = self.sample()
        data["clocks"][1].update(time=1., weapon="weapon_ak47")
        rows = [{"item_def_index": 7, "game_time": 1. + i * .01} for i in range(2)]
        with tempfile.TemporaryDirectory() as directory:
            motion = Path(directory) / "native.json"
            shots = Path(directory) / "shots.parquet"
            motion.write_text(json.dumps(data), encoding="utf-8")
            pd.DataFrame(rows).to_parquet(shots)
            with self.assertRaisesRegex(ValueError, "ambiguous"):
                compare(motion, shots)

    def test_non_overlapping_recordings_rejected(self):
        data = self.sample()
        data["clocks"][1].update(time=1., weapon="weapon_ak47")
        with tempfile.TemporaryDirectory() as directory:
            motion = Path(directory) / "native.json"
            shots = Path(directory) / "shots.parquet"
            motion.write_text(json.dumps(data), encoding="utf-8")
            pd.DataFrame([{"item_def_index": 7, "game_time": 2.}]).to_parquet(shots)
            with self.assertRaisesRegex(ValueError, "missing"):
                compare(motion, shots)


if __name__ == "__main__":
    unittest.main()
