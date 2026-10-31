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
 def test_no_shots_does_not_pass_gating(self):
  with tempfile.TemporaryDirectory() as d:
   result=audit(self.write(d,[{'schema':'bot-aim-telemetry-v1'}]))
   self.assertEqual(result['fire_gate']['status'],'no-classified-gun-fire')
 def test_blocked_callback_reported(self):
  with tempfile.TemporaryDirectory() as d:
   result=audit(self.write(d,[{'schema':'bot-aim-telemetry-v1'},
    {'kind':'weapon_fire_callback','event_weapon':'weapon_ak47','allow_fire':False,'model_fire_suppression_owned':True}]))
   self.assertEqual(result['fire_gate']['status'],'blocked-state-fire-callbacks')
   self.assertEqual(result['fire_gate']['blocked_and_suppression_owned'],1)
 def test_unknown_gate_state_not_accepted(self):
  with tempfile.TemporaryDirectory() as d:
   result=audit(self.write(d,[{'schema':'bot-aim-telemetry-v1'},
    {'kind':'weapon_fire_callback','event_weapon':'weapon_ak47'}]))
   self.assertEqual(result['fire_gate']['status'],'insufficient-gate-state')
 def test_effective_test_override_not_lost(self):
  with tempfile.TemporaryDirectory() as d:
   result=audit(self.write(d,[{'schema':'bot-aim-telemetry-v1'},
    {'kind':'weapon_fire_callback','event_weapon':'weapon_ak47','allow_fire':True,'effective_allow_fire':False,'model_fire_suppression_owned':True}]))
   self.assertEqual(result['fire_gate']['blocked_and_suppression_owned'],1)
if __name__=='__main__':unittest.main()
