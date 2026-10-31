import sys,json,tempfile,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from audit_burst_posture import audit
class PostureAudit(unittest.TestCase):
 def sample(self,folder,weapon='weapon_ak47',alternate=False):
  rows=[dict(kind='session',tick_interval=.015625)]
  rows += [dict(kind='weapon_fire_callback',event_weapon=weapon,slot=0,time=i*.1) for i in range(30)]
  rows += [dict(kind='server_tick_observation',slot=0,time=i/64,native_crouching=(i%2==0) if alternate else True,view_offset_z=46.) for i in range(192)]
  p=Path(folder)/'capture.jsonl';p.write_text('\n'.join(json.dumps(r) for r in rows));return p
 def test_stable_branch(self):
  with tempfile.TemporaryDirectory() as d:self.assertEqual(audit(self.sample(d))[0]['intent_switches'],0)
 def test_alternation_is_reported(self):
  with tempfile.TemporaryDirectory() as d:self.assertGreater(audit(self.sample(d,alternate=True))[0]['intent_switches'],100)
 def test_wrong_weapon_rejected(self):
  with tempfile.TemporaryDirectory() as d:
   with self.assertRaises(ValueError):audit(self.sample(d,weapon='weapon_tec9'))
if __name__=='__main__':unittest.main()
