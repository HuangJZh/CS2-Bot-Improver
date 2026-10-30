using System.Numerics;
using BotAimImprover;
int count=0;
void Check(bool ok,string name) { if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name); }
Vector3 eye=Vector3.Zero,target=new(1000,200,0);
Check(MathF.Abs(AimMath.Delta(new(0,-179),new(0,179)).Y-2)<.001f,"yaw wrap chooses short turn");
Check(AimMath.Ease(0)==0 && AimMath.Ease(1)==1,"movement endpoints");


foreach(var level in new[]{"Low","Medium","High"})
{
 var profile=HumanAimProfile.For(level);
 var model=new HumanAimModel(profile,Vector2.Zero,0,42);
 var output=model.Step(.01f,eye,true);
 Check(!output.AllowFire && output.Phase=="perception",level+" cannot fire without observations");
 model.Observe(.02f,target);
 output=model.Step(.02f+profile.SensorDelay-.005f,eye,true);
 Check(!output.AllowFire && output.Angles==Vector2.Zero,level+" observation delay respected");
 output=model.Step(.02f+profile.SensorDelay+.001f,eye,true);
 Check(!output.AllowFire && output.Phase=="reaction",level+" separate reaction gate");
 float firstFire=-1;Vector2 previous=output.Angles;float maxTurn=0;
 for(int tick=1;tick<180;tick++)
 {
  float now=.1f+tick/128f;
  if(model.NeedsObservation(now))model.Observe(now,target);
  output=model.Step(now,eye,true);
  float turn=AimMath.Delta(output.Angles,previous).Length();maxTurn=MathF.Max(maxTurn,turn);previous=output.Angles;
  if(output.AllowFire && firstFire<0)firstFire=now;
  CheckFinite(output.Angles);
 }
 Check(firstFire>profile.Reaction && firstFire<.8f,level+" finite first-shot window after reaction");
 Check(maxTurn<=profile.MaxSpeed*.05f+.001f,level+" bounded movement without snaps");
 Check(AimMath.Delta(output.Angles,AimMath.Angles(eye,target)).Length()<1.5f,level+" converges near stationary target");
 float last=model.LastVisible;
 output=model.Step(last+.4f,eye,false);
 Check(!output.AllowFire && output.Phase=="memory-expired",level+" occlusion blocks firing and expires memory");
}
void CheckFinite(Vector2 a) { if(!float.IsFinite(a.X+a.Y))throw new Exception("non-finite angle"); }
var m1=new HumanAimModel(HumanAimProfile.For("High"),Vector2.Zero,0,123);
var m2=new HumanAimModel(HumanAimProfile.For("High"),Vector2.Zero,0,123);
for(int i=0;i<200;i++)
{
 float now=i/128f;var p=new Vector3(1000,now<.8f ? now*200 : 160-(now-.8f)*200,0);
 m1.Observe(now,p);m2.Observe(now,p);
 var a=m1.Step(now,eye,true);var b=m2.Step(now,eye,true);
 if(a!=b)throw new Exception("nondeterministic simulation");
}
Check(true,"same seed and observations reproduce identical trajectory");
var moving=new HumanAimModel(HumanAimProfile.For("High"),Vector2.Zero,0,1);
AimOutput before=default,after=default;
for(int i=0;i<150;i++)
{
 float now=i/128f;var p=new Vector3(1000,now<1 ? now*200 : 200-(now-1)*200,0);
 moving.Observe(now,p);var result=moving.Step(now,eye,true);
 if(i==127)before=result;if(i==129)after=result;
}
Check(after.Angles.Y>=before.Angles.Y,"unexpected reversal cannot be corrected instantly");
var profileHigh=HumanAimProfile.For("High");var profileLow=HumanAimProfile.For("Low");
Check(profileHigh.MotorError<profileLow.MotorError && profileHigh.SensorDelay<profileLow.SensorDelay,"difficulty controls precision and sensory delay");
Console.WriteLine($"{count} checks passed");
