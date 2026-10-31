import unittest,tempfile,json,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from audit_aim_capture import audit
class CaptureAudit(unittest.TestCase):
 def write(self,folder,rows):
  p=Path(folder)/'capture.jsonl';p.write_text('\n'.join(json.dumps(r) for r in rows),encoding='utf-8');return p
 def test_wrap_and_event_count(self):
  with tempfile.TemporaryDirectory() as d:
   p=self.write(d,[{'schema':'bot-aim-telemetry-v1','kind':'session'},{'kind':'server_tick_observation','tick':1,'slot':1,'pawn':42,'phase':'tracking','yaw':-179,'planned_yaw':179},{'kind':'weapon_fire_callback'}])
   r=audit(p);self.assertEqual(r['tracking_yaw_error']['1']['median_deg'],2);self.assertEqual(r['kinds']['weapon_fire_callback'],1)
 def test_duplicate_rejected(self):
  with tempfile.TemporaryDirectory() as d:
   row={'kind':'server_tick_observation','tick':1,'slot':1,'pawn':42}
   p=self.write(d,[{'schema':'bot-aim-telemetry-v1'},row,row])
   with self.assertRaises(ValueError):audit(p)
 def test_missing_schema_rejected(self):
  with tempfile.TemporaryDirectory() as d:
   with self.assertRaises(ValueError):audit(self.write(d,[{'kind':'session'}]))
if __name__=='__main__':unittest.main()
