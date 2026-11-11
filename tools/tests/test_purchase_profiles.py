"""Verify each difficulty preserves affordable preferred primary choices."""
import re
import unittest
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
class PurchaseProfiles(unittest.TestCase):
 def test_preferences(self):
  for level in ['Low','Medium','High']:
   text=(ROOT/'overrides'/level/'botprofile.db').read_text()
   self.assertNotRegex(text,r'WeaponPreference\s*=\s*(aug|sg556)\b')
   blocks=re.findall(r'(?m)^Template\s+(\w+)[^\n]*\n([\s\S]*?)^End\b',text)
   tested=0
   for role,body in blocks:
    weapons=re.findall(r'WeaponPreference\s*=\s*(\w+)',body)
    if not weapons:continue
    tested+=1
    self.assertEqual(set(weapons[:4]),{'awp','ak47','m4a1','m4a1_silencer'},(level,role))
    self.assertEqual(len(weapons),len(set(weapons)),(level,role))
    for allowed in [{'ak47','awp'},{'m4a1','m4a1_silencer','awp'}]:
     first=next(w for w in weapons if w in allowed|{'xm1014','negev','p90','mac10','mp9','ssg08'})
     self.assertIn(first,allowed,(level,role))
   self.assertEqual(tested,8)
if __name__=='__main__':unittest.main()
