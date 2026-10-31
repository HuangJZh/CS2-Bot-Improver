"""Segment approximate visibility without hiding truncation or target loss."""
import numpy as np

def segments(ticks,rounds,eligible,visible,observer_alive,target_alive,lost_ticks):
 ticks=np.asarray(ticks);rounds=np.asarray(rounds);eligible=np.asarray(eligible,bool);visible=np.asarray(visible,bool)
 rows=[];active=None;previous=None
 def finish(end,reason,censored=False):
  nonlocal active
  if active is None:return
  active.update(end_tick=int(end),end_reason=reason,right_censored=bool(censored));rows.append(active);active=None
 for i,tick in enumerate(ticks):
  continuous=previous is not None and tick==ticks[previous]+1 and rounds[i]==rounds[previous]
  if active is not None and not continuous:
   consecutive=previous is not None and tick==ticks[previous]+1
   if consecutive and (not observer_alive[i] or not target_alive[i]):
    finish(tick,"observer_dead" if not observer_alive[i] else "target_dead")
   else:
    reason="round_boundary" if previous is not None and rounds[i]!=rounds[previous] else "record_gap"
    finish(ticks[previous],reason,True)
  if not eligible[i]:
   reason="observer_dead" if not observer_alive[i] else "target_dead" if not target_alive[i] else "phase_boundary"
   finish(tick,reason,reason=="phase_boundary");previous=i;continue
  if visible[i]:
   if active is None:
    active=dict(start_tick=int(tick),last_visible_tick=int(tick),visible_ticks=0,round=int(rounds[i]),left_censored=not(continuous and eligible[previous]))
   active["last_visible_tick"]=int(tick);active["visible_ticks"]+=1
  elif active is not None and tick-active["last_visible_tick"]>lost_ticks:
   finish(active["last_visible_tick"],"sight_lost")
  previous=i
 if active is not None:finish(ticks[-1],"record_end",True)
 return rows
