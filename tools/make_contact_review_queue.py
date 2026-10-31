"""Stable IDs and empty review fields. Does not mark visibility as validated."""
import argparse,json
from pathlib import Path
import pandas as pd

def make(matches,candidates,out):
 rows=[]
 for match in sorted(matches.iterdir()):
  path=candidates/match.name/'contact-candidates.parquet'
  if not path.exists():continue
  meta=json.loads((match/'manifest.json').read_text());data=pd.read_parquet(path)
  if (data.end_tick<data.start_tick).any() or (data.visible_tick_fraction>1).any():raise ValueError('Invalid candidate interval')
  for row in data.to_dict('records'):
   row.update(contact_id=meta['source_sha256'][:16]+':'+row['observer']+':'+row['target']+':'+str(row['start_tick']),map=meta['header']['map_name'],review_status='not_reviewed',reviewed_first_visible_tick=None,review_notes='')
   rows.append(row)
 data=pd.DataFrame(rows)
 if data.contact_id.duplicated().any():raise ValueError('Duplicate contact ID')
 data.to_csv(out,index=False);print('Review queue',len(data),out)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('matches',type=Path);p.add_argument('--candidate-root',type=Path,required=True);p.add_argument('--out',type=Path,required=True);a=p.parse_args();make(a.matches,a.candidate_root,a.out)
