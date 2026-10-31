"""Recorded live-round windows; keep endpoint censoring explicit."""
import numpy as np

def live_round_intervals(clock,freeze_ends,round_ends,competitive_start):
 ticks=clock.tick.to_numpy();last=int(ticks[-1]);lookup=clock.set_index('tick')
 begins=[]
 for tick in sorted(set(freeze_ends.tick.dropna().astype(int))):
  if tick<competitive_start or tick not in lookup.index:continue
  row=lookup.loc[tick]
  if bool(row.is_warmup_period):continue
  begins.append(tick)
 ends=round_ends
 if 'winner' in ends:ends=ends[ends.winner.notna()]
 endings=sorted(set(ends.tick.dropna().astype(int)))
 result=[]
 for i,start in enumerate(begins):
  next_start=begins[i+1] if i+1<len(begins) else last+1
  possible=[t for t in endings if start<=t<next_start]
  result.append(dict(start_tick=start,end_tick=possible[0] if possible else next_start-1,right_censored=not bool(possible)))
 return result

def live_mask(ticks,intervals):
 ticks=np.asarray(ticks);mask=np.zeros(len(ticks),bool)
 for row in intervals:mask|=(ticks>=row['start_tick'])&(ticks<=row['end_tick'])
 return mask
