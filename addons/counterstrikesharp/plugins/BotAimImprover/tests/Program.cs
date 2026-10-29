using System.Numerics;
using BotAimImprover;
void Check(bool ok,string name) { if(!ok) throw new Exception(name); Console.WriteLine("PASS "+name); }
Check(SmokeAimPolicy.Blocks(new(-200,0,0),new(200,0,0),Vector3.Zero,155),"smoke between shooter and target");
Check(!SmokeAimPolicy.Blocks(new(200,0,0),new(400,0,0),Vector3.Zero,155),"smoke behind shooter leaves visible aim unchanged");
Check(SmokeAimPolicy.Blocks(Vector3.Zero,new(400,0,0),Vector3.Zero,155),"shooter inside smoke");
Check(!SmokeAimPolicy.Blocks(new(-200,300,0),new(200,300,0),Vector3.Zero,155),"nearby smoke off the firing line does not trigger penalty");
Check(SmokeAimPolicy.Radius(20)==0 && SmokeAimPolicy.Radius(-1)==0,"expired/unstarted smoke excluded");
Check(SmokeAimPolicy.Error(10)==35 && SmokeAimPolicy.Error(5000)==100,"bounded aiming error");
Check(SmokeAimPolicy.Error(1000)>SmokeAimPolicy.Error(100),"distance increases uncertainty");
