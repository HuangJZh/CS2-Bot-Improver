"""Visualize model/recorded-view coupling; not a human skill comparison."""
import argparse,json
from pathlib import Path
import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt

def main():
 p=argparse.ArgumentParser();p.add_argument('--before',type=Path,required=True);p.add_argument('--after',type=Path,required=True);p.add_argument('--out',type=Path,required=True);p.add_argument('--slot',type=int,default=1);a=p.parse_args()
 fig,axes=plt.subplots(1,2,figsize=(11,4),sharey=True)
 for ax,file,title in zip(axes,[a.before,a.after],['Point-target path (controlled fixture)','Experimental direct-view path (controlled fixture)']):
  rows=[json.loads(x) for x in file.read_text().splitlines()];rows=[r for r in rows if r.get('slot')==a.slot and r.get('phase')]
  start,base=rows[0]['time'],rows[0]['yaw'];rows=[r for r in rows if r['time']-start<=6]
  t=[r['time']-start for r in rows];view=[(r['yaw']-base+180)%360-180 for r in rows];plan=[(r['planned_yaw']-base+180)%360-180 for r in rows]
  ax.plot(t,plan,label='Planned yaw',linewidth=2);ax.plot(t,view,label='Recorded yaw',linestyle='--',linewidth=1.5);ax.set(title=title,xlabel='Seconds from first model observation',ylabel='Yaw relative to initial recorded view (degrees)');ax.grid(alpha=.2);ax.legend()
 fig.suptitle('Model-to-view coupling check — scripted target step, not a skill benchmark');fig.tight_layout();fig.savefig(a.out,dpi=160)
if __name__=='__main__':main()
