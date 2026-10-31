import sys,unittest
from pathlib import Path
import pandas as pd
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from round_phases import live_round_intervals,live_mask
class RoundPhases(unittest.TestCase):
 def test_post_round_excluded_and_final_event_included(self):
  clock=pd.DataFrame({'tick':range(30),'is_warmup_period':[False]*30})
  intervals=live_round_intervals(clock,pd.DataFrame({'tick':[2,20]}),pd.DataFrame({'tick':[0,10,25],'winner':[None,'CT','T']}),2)
  mask=live_mask(clock.tick,intervals);self.assertTrue(mask[10]);self.assertFalse(mask[11]);self.assertFalse(mask[19]);self.assertTrue(mask[20]);self.assertFalse(mask[26])
 def test_restart_and_warmup_excluded(self):
  clock=pd.DataFrame({'tick':range(30),'is_warmup_period':[True]*5+[False]*25})
  intervals=live_round_intervals(clock,pd.DataFrame({'tick':[1,7,20]}),pd.DataFrame({'tick':[10,25]}),15)
  self.assertEqual(len(intervals),1);self.assertEqual(intervals[0]['start_tick'],20)
 def test_recording_end_is_censored(self):
  clock=pd.DataFrame({'tick':range(10),'is_warmup_period':[False]*10})
  intervals=live_round_intervals(clock,pd.DataFrame({'tick':[2]}),pd.DataFrame({'tick':[]}),0)
  self.assertTrue(intervals[0]['right_censored']);self.assertEqual(intervals[0]['end_tick'],9)
if __name__=='__main__':unittest.main()
