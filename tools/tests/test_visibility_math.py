import sys,unittest
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import numpy as np
import pandas as pd
from probe_demo_visibility import in_fov,intersects_smoke,smoke_intervals
class VisibilityMathTests(unittest.TestCase):
 def test_front_and_rear(self):
  eye=np.zeros(3)
  self.assertTrue(in_fov(eye,np.array([100,0,0]),0,0,90,4/3))
  self.assertFalse(in_fov(eye,np.array([-100,0,0]),0,0,90,4/3))
 def test_scope_and_aspect(self):
  eye=np.zeros(3);point=np.array([100,110,0])
  self.assertFalse(in_fov(eye,point,0,0,90,4/3))
  self.assertTrue(in_fov(eye,point,0,0,90,16/9))
  self.assertFalse(in_fov(eye,point,0,0,40,16/9))
 def test_smoke_segment(self):
  a=np.array([-100.,0,0]);b=np.array([100.,0,0])
  self.assertTrue(intersects_smoke(a,b,np.zeros(3),20))
  self.assertFalse(intersects_smoke(a,b,np.array([200.,0,0]),20))
  self.assertFalse(intersects_smoke(a,b,np.array([0.,100,0]),20))
 def test_missing_expiry_round_boundary(self):
  smoke=pd.DataFrame([dict(entityid=1,tick=10,x=0,y=0,z=0)])
  expired=pd.DataFrame(columns=["entityid","tick"])
  self.assertEqual(smoke_intervals(smoke,expired,[40],100)[0][1],40)
 def test_expiry_and_reused_id(self):
  smoke=pd.DataFrame([dict(entityid=1,tick=t,x=0,y=0,z=0) for t in [10,50]])
  expired=pd.DataFrame([dict(entityid=1,tick=80)])
  result=smoke_intervals(smoke,expired,[],100)
  self.assertEqual(result[0][1],50);self.assertEqual(result[1][1],80)
if __name__=="__main__":unittest.main()
