import sys,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from contact_segments import segments
class Contacts(unittest.TestCase):
 def run_case(self,visible,eligible=None,rounds=None,oa=None,ta=None,ticks=None):
  n=len(visible);return segments(ticks or list(range(n)),rounds or [0]*n,eligible or [True]*n,visible,oa or [True]*n,ta or [True]*n,2)
 def test_recording_start_is_censored(self):
  r=self.run_case([True,True])[0];self.assertTrue(r['left_censored']);self.assertTrue(r['right_censored'])
 def test_known_hidden_before_contact(self):
  r=self.run_case([False,True,True,False,False,False])[0];self.assertFalse(r['left_censored']);self.assertEqual(r['end_reason'],'sight_lost')
 def test_short_occlusion_retained(self):
  r=self.run_case([False,True,False,True,False,False,False]);self.assertEqual(len(r),1);self.assertEqual(r[0]['visible_ticks'],2)
 def test_failure_without_shooting_preserved(self):
  r=self.run_case([False,True,False],[True,True,False],oa=[True,True,False])[0];self.assertEqual(r['end_reason'],'observer_dead');self.assertEqual(r['end_tick'],2)
 def test_death_on_round_transition_is_not_lost(self):
  r=self.run_case([False,True,False],[True,True,False],rounds=[0,0,1],oa=[True,True,False])[0]
  self.assertEqual(r['end_reason'],'observer_dead');self.assertEqual(r['end_tick'],2);self.assertFalse(r['right_censored'])
 def test_rounds_do_not_merge(self):
  r=self.run_case([False,True,True,True],rounds=[0,0,1,1]);self.assertEqual(len(r),2);self.assertEqual(r[0]['end_reason'],'round_boundary')
 def test_missing_ticks_censor(self):
  r=self.run_case([False,True,True],ticks=[0,1,10]);self.assertEqual(len(r),2);self.assertEqual(r[0]['end_reason'],'record_gap');self.assertTrue(r[1]['left_censored'])
if __name__=='__main__':unittest.main()
