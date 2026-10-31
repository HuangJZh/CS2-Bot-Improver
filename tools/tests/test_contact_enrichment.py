import sys,json,tempfile,unittest
from pathlib import Path
import pandas as pd
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from enrich_contact_candidates import enrich
class Enrichment(unittest.TestCase):
 def test_grenade_damage_not_aim_damage_and_fire_attribution_stays_uncertain(self):
  with tempfile.TemporaryDirectory() as d:
   root=Path(d)
   (root/'manifest.json').write_text(json.dumps({'tick_interval_seconds':.015625}))
   (root/'contact-candidate-summary.json').write_text(json.dumps({'rows':2}))
   contacts=[dict(start_tick=10,end_tick=15,observer='1',target='2',left_censored=False,right_censored=False),dict(start_tick=12,end_tick=20,observer='1',target='3',left_censored=False,right_censored=False)]
   pd.DataFrame(contacts).to_parquet(root/'contact-candidates.parquet')
   pd.DataFrame([dict(tick=13,user_steamid='1',weapon='ak47')]).to_parquet(root/'weapon_fire.parquet')
   pd.DataFrame([dict(tick=10,attacker_steamid='1',user_steamid='2',weapon='hegrenade'),dict(tick=14,attacker_steamid='1',user_steamid='3',weapon='ak47')]).to_parquet(root/'player_hurt.parquet')
   pd.DataFrame([dict(tick=15,attacker_steamid='2',user_steamid='1',weapon='ak47')]).to_parquet(root/'player_death.parquet')
   frames=[dict(tick=t,steamid=s,X=(s-1)*100.,Y=0.,Z=0.,pitch=0.,yaw=0.,duck_amount=0.,active_weapon_name='AK-47',is_scoped=False,fov=0.) for t in [10,12] for s in [1,2,3]]
   pd.DataFrame(frames).to_parquet(root/'frames.parquet')
   enrich(root);rows=pd.read_parquet(root/'contact-candidates.parquet').set_index('target')
   self.assertEqual(rows.loc['2','pair_damage_count'],0)
   self.assertTrue(rows.loc['2','no_damage_before_observer_death'])
   self.assertTrue(rows.loc['2','observer_killed_by_this_target'])
   self.assertEqual(rows.loc['3','pair_damage_count'],1)
   self.assertFalse(rows.loc['3','observer_killed_by_this_target'])
   self.assertEqual(rows.loc['3','competing_candidate_targets_at_first_fire'],1)
   self.assertTrue(rows.loc['3','first_fire_pair_damage_same_or_next_tick'])
   self.assertTrue(rows.loc['3','fire_target_unconfirmed'])
if __name__=='__main__':unittest.main()
