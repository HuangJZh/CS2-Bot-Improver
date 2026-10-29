import importlib.util
from pathlib import Path
import re
import struct
import unittest
import zlib

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location("pack", ROOT / "tools/package_difficulty.py")
pack = importlib.util.module_from_spec(spec)
spec.loader.exec_module(pack)


class DifficultyTests(unittest.TestCase):
    def test_no_personality_bypasses_reaction_or_accuracy(self):
        reactions = []
        for level in ("Low", "Medium", "High"):
            text = (ROOT / "overrides" / level / "botprofile.db").read_text()
            for name in ("ReactionTime", "AttackDelay", "AimFocusInitial", "AimFocusOffsetScale"):
                values = [float(v) for v in re.findall(r"(?im)^\s*" + name + r"\s*=\s*(\S+)", text)]
                self.assertGreater(len(values), 5)
                self.assertTrue(all(v > 0 for v in values), (level, name))
            reactions.append(float(re.search(r"ReactionTime\s*=\s*(\S+)", text)[1]))
            bt = (ROOT / "overrides/scripts" / level / "bt_config.kv3").read_text()
            self.assertIn(f"reaction_time = {reactions[-1]}", bt)
            self.assertIn("aim_new_target_angle_tolerance = 90.0", bt)
        self.assertGreater(reactions[0], reactions[1])
        self.assertGreater(reactions[1], reactions[2])

    def test_vpk_roundtrip_offsets_crc_and_selected_config(self):
        for level in ("Low", "Medium", "High"):
            expected = pack.difficulty_files(level)
            data = pack.make_vpk(expected)
            self.assertEqual(data, pack.make_vpk(dict(reversed(list(expected.items())))))
            magic, version, tree_size, data_size, *_ = struct.unpack_from("<7I", data)
            self.assertEqual((magic, version), (0x55AA1234, 2))
            self.assertEqual(len(data), 28 + tree_size + data_size)
            pos, found = 28, {}
            def string():
                nonlocal pos
                end = data.index(0, pos)
                value = data[pos:end].decode()
                pos = end + 1
                return value
            while (ext := string()):
                while (path := string()):
                    while (name := string()):
                        crc, preload, archive, offset, size, terminator = struct.unpack_from("<IHHIIH", data, pos)
                        pos += 18
                        self.assertEqual((preload, archive, terminator), (0, 0x7FFF, 0xFFFF))
                        blob = data[28 + tree_size + offset:28 + tree_size + offset + size]
                        self.assertEqual(zlib.crc32(blob), crc)
                        found[("" if path == " " else path + "/") + name + "." + ext] = blob
            self.assertEqual(found, expected)
            self.assertIn(f"bot_human_difficulty {level.lower()}".encode(), found["cfg/bot_human_active.cfg"])

    def test_all_modes_execute_selected_difficulty(self):
        configs = list((ROOT / "cfg").glob("my_bot_*config*.cfg"))
        self.assertEqual(len(configs), 6)
        for config in configs:
            text = config.read_text()
            self.assertIn("exec bot_human_active.cfg", text)
            self.assertIn("weapon_accuracy_nospread 0", text)


if __name__ == "__main__":
    unittest.main()
