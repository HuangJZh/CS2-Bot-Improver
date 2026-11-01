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
var center=new VisualLocalization(.1f,777).Estimate(0,eye,new(1000,0,0),Vector2.Zero,4);
var peripheral=new VisualLocalization(.1f,777).Estimate(0,eye,new(1000,0,0),new(0,50),4);
Check(peripheral.UncertaintyDegrees>center.UncertaintyDegrees,"peripheral localization is less certain than central localization");
var distant=new VisualLocalization(.1f,777).Estimate(0,eye,new(3000,0,0),Vector2.Zero,4);
Check(distant.UncertaintyDegrees>center.UncertaintyDegrees,"smaller angular target increases localization uncertainty");
var trunk=new VisualLocalization(.1f,777).Estimate(0,eye,new(1000,0,0),Vector2.Zero,8);
Check(trunk.UncertaintyDegrees<center.UncertaintyDegrees,"larger part-size proxy lowers localization uncertainty");
Check(center.Position!=new Vector3(1000,0,0),"visual localization has its own error before motor planning");
var exact=new VisualLocalization(0,1).Estimate(0,eye,target,Vector2.Zero,4);
Check(Vector3.Distance(exact.Position,target)<.001f,"zero visual uncertainty does not introduce a target offset");
var visualA=new VisualLocalization(.1f,13);var visualB=new VisualLocalization(.1f,13);
VisualEstimate v=default,previousVisual=default;
float largestVisualStep=0;
for(int i=0;i<2000;i++)
{
 float now=i*.001f;v=visualA.Estimate(now,eye,new(1000,0,0),Vector2.Zero,4);
 var repeat=visualB.Estimate(now,eye,new(1000,0,0),Vector2.Zero,4);
 if(v!=repeat)throw new Exception("visual process not reproducible");
 if(i>0)largestVisualStep=MathF.Max(largestVisualStep,AimMath.Delta(AimMath.Angles(eye,v.Position),AimMath.Angles(eye,previousVisual.Position)).Length());
 previousVisual=v;
}
Check(true,"visual localization reproducible from same observed samples and seed");
Check(largestVisualStep<.005f,"localization error drifts coherently rather than resampling white noise");
var visualModel=new HumanAimModel(profileHigh,Vector2.Zero,0,12);
Check(visualModel.ObserveVisual(0,eye,target,Vector2.Zero,4).HasValue,"first visible sample enters perceptual estimator");
Check(!visualModel.ObserveVisual(.001f,eye,new(1000,800,0),Vector2.Zero,4).HasValue,"oversampling cannot replace delayed observations");
var delayedVisual=visualModel.Step(profileHigh.SensorDelay-.001f,eye,true);
Check(delayedVisual.Phase=="perception" && delayedVisual.Angles==Vector2.Zero && !delayedVisual.AllowFire,"noisy visual estimate still waits for sensory delay");
bool backwardsRejected=false;
try{visualA.Estimate(0,eye,target,Vector2.Zero,4);}catch(ArgumentException){backwardsRejected=true;}
Check(backwardsRejected,"visual estimator rejects backwards clock instead of reusing future state");
foreach(var level in new[]{"Low","Medium","High"})
{
 var observed=new HumanAimModel(HumanAimProfile.For(level),Vector2.Zero,0,671);
 var view=Vector2.Zero;AimOutput result=default;
 for(int i=0;i<256;i++)
 {
  float now=i/128f;
  observed.ObserveVisual(now,eye,target,view,4);
  result=observed.Step(now,eye,true);view=result.Angles;CheckFinite(view);
 }
 Check(result.AllowFire && AimMath.Delta(view,AimMath.Angles(eye,target)).Length()<2,
  level+" delayed noisy observations support bounded stationary engagement");
 result=observed.Step(observed.LastVisible+.4f,eye,false);
 Check(!result.AllowFire && result.Phase=="memory-expired",level+" uncertain localization does not extend hidden target memory");
}
FireDecision FireAt(float error,float radius,AimWeaponClass weapon,bool underFire=false,float uncertainty=0,bool visible=true,float motorError=.16f)
 =>HumanFireDecision.Evaluate(new(0,error),Vector2.Zero,radius,uncertainty,visible,.05f,false,new(weapon,underFire),motorError);
Check(FireAt(.4f,.5f,AimWeaponClass.Rifle).Allow && !FireAt(.4f,.5f,AimWeaponClass.Sniper).Allow,
 "sniper requests tighter expected alignment than rifle");
var plannedOnly=FireAt(.3f,.5f,AimWeaponClass.Rifle);
var withActual=HumanFireDecision.Evaluate(new(0,.3f),Vector2.Zero,.5f,0,true,.05f,false,new(AimWeaponClass.Rifle),.16f,2.0f,.5f);
Check(plannedOnly.Allow && withActual.Allow && withActual.ActualErrorDegrees is >1.9f,
 "actual crosshair error is recorded without vetoing an intended-aligned shot");
Check(plannedOnly.ActualErrorDegrees==null,"without actual-view input no crosshair error is reported");
Check(FireAt(.6f,1,AimWeaponClass.Rifle).Allow && !FireAt(.6f,.1f,AimWeaponClass.Rifle).Allow,
 "angular target size changes fire acceptance independently of flick progress");
Check(!FireAt(.8f,.5f,AimWeaponClass.Rifle).Allow && FireAt(.8f,.5f,AimWeaponClass.Rifle,true).Allow,
 "recent incoming damage can trade precision for urgency");
Check(!FireAt(0,.5f,AimWeaponClass.Rifle,true,0,false).Allow,"urgency cannot bypass lost visibility");
Check(FireAt(.7f,.5f,AimWeaponClass.Rifle).Allow && !FireAt(.7f,.5f,AimWeaponClass.Rifle,false,.3f).Allow,
 "uncertain localization requires a more central intended shot");
Check(FireAt(.45f,.05f,AimWeaponClass.Rifle).Allow && !FireAt(.9f,.05f,AimWeaponClass.Rifle).Allow,
 "very small distant targets keep a bounded aim floor while still requiring central aim");
Check(FireAt(.4f,.05f,AimWeaponClass.Rifle,false,0,true,.32f).ToleranceDegrees > FireAt(.4f,.05f,AimWeaponClass.Rifle).ToleranceDegrees,
 "less precise profile motor error raises the admissible fire envelope for small targets");
Check(!HumanFireDecision.Evaluate(Vector2.Zero,Vector2.Zero,.5f,0,true,.21f,false,default).Allow,
 "aligned but stale target cannot authorize fire");
Check(!HumanFireDecision.Evaluate(Vector2.Zero,Vector2.Zero,.5f,0,true,.05f,true,default).Allow,
 "pending corrective movement is not mistaken for readiness");
Check(HumanFireContext.Classify("weapon_awp")==AimWeaponClass.Sniper && HumanFireContext.Classify("weapon_deagle")==AimWeaponClass.PrecisePistol
 && HumanFireContext.Classify("weapon_m4a1_silencer")==AimWeaponClass.Rifle,"weapon adapter distinguishes precise and automatic weapons");
var movingLead=new HumanAimModel(profileHigh,Vector2.Zero,0,91);
bool leadFired=false;int leadAllows=0;
for(int i=0;i<256;i++)
{
 float now=i/128f;var movingPos=new Vector3(1000,now*250f,0);
 movingLead.Observe(now,movingPos);
 var movingOutput=movingLead.Step(now,eye,true,new(AimWeaponClass.Rifle));
 if(movingOutput.AllowFire && !leadFired)
 {
  leadFired=true;
 }
 if(movingOutput.AllowFire)leadAllows++;
}
Check(leadFired && leadAllows>32,"moving-target tracking still permits fire without artificial projectile lead");
foreach(float distance in new[]{600f,1200f,2400f})
{
 var beliefProfile=profileHigh with {Prediction=1f};
 var rifleModel=new HumanAimModel(beliefProfile,Vector2.Zero,0,91);
 var pistolModel=new HumanAimModel(beliefProfile,Vector2.Zero,0,91);
 Vector3 current=default;
 for(int i=0;i<256;i++)
 {
  float now=i/128f;current=new Vector3(distance,now*250f,0);
  rifleModel.Observe(now,current);pistolModel.Observe(now,current);
  rifleModel.Step(now,eye,true,new(AimWeaponClass.Rifle));
  pistolModel.Step(now,eye,true,new(AimWeaponClass.Pistol));
 }
 float error=AimMath.Delta(rifleModel.LastDesiredAngles,AimMath.Angles(eye,current)).Length();
 Console.WriteLine($"Moving belief at {distance}u: angular error {error:F4} degrees");
 Check(error<.01f,$"{distance}u moving head belief compensates observation age without aiming into the future");
 Check(AimMath.Delta(rifleModel.LastDesiredAngles,pistolModel.LastDesiredAngles).Length()<.0001f,
  $"{distance}u hitscan weapon class does not change the believed target bearing");
}
var preparedPlan=new HumanAimModel(profileHigh,Vector2.Zero,0,7);
preparedPlan.Observe(.02f,new Vector3(1000,100,0));
preparedPlan.Step(.02f+profileHigh.SensorDelay+.001f,eye,true);
var unexpectedPlan=new HumanAimModel(profileHigh,Vector2.Zero,0,7);
unexpectedPlan.Observe(.02f,new Vector3(100,1000,0));
unexpectedPlan.Step(.02f+profileHigh.SensorDelay+.001f,eye,true);
Check(preparedPlan.ContactKind=="prepared" && unexpectedPlan.ContactKind=="unexpected",
 "contact gap classifies prepared versus unexpected engagement");
Check(unexpectedPlan.PlanStartedAt-preparedPlan.PlanStartedAt>.03f,
 "unexpected contact adds reaction delay relative to prepared contact");
(float Time,AimOutput Output) FirstFire(HumanAimProfile profile,Vector3 point,AimWeaponClass weapon,int seed=45)
{
 var model=new HumanAimModel(profile,Vector2.Zero,0,seed);
 for(int i=0;i<192;i++)
 {
  float now=i/128f;model.Observe(now,point);
  var output=model.Step(now,eye,true,new(weapon));
  if(output.AllowFire)return(now,output);
 }
 throw new Exception("no first fire in controlled stationary case");
}
var rifleFirst=FirstFire(profileHigh,target,AimWeaponClass.Rifle);
var sniperFirst=FirstFire(profileHigh,target,AimWeaponClass.Sniper);
Check(sniperFirst.Time>=rifleFirst.Time,"weapon precision requirement changes gate without speeding up motor movement");
var preparedFirst=FirstFire(profileHigh,new(1000,.1f,0),AimWeaponClass.Rifle);
Check(preparedFirst.Time<rifleFirst.Time,"already aligned contact need not wait for a fixed fraction of a flick");
bool imperfectFire=false;
for(int seed=0;seed<32;seed++)
{
 var first=FirstFire(profileHigh with{MotorError=3},target,AimWeaponClass.Rifle,seed);
 if(AimMath.Delta(first.Output.Angles,AimMath.Angles(eye,target)).Length()>1)imperfectFire=true;
}
Check(imperfectFire,"intended alignment does not secretly reject every motor-error miss");
var delayedSizeA=new HumanAimModel(profileHigh,Vector2.Zero,0,25);
var delayedSizeB=new HumanAimModel(profileHigh,Vector2.Zero,0,25);
for(int i=0;i<=25;i++)
{
 float now=i/128f;delayedSizeA.Observe(now,target,4);delayedSizeB.Observe(now,target,4);
 delayedSizeA.Step(now,eye,true);delayedSizeB.Step(now,eye,true);
}
delayedSizeA.Observe(.25f,target,100);delayedSizeB.Observe(.25f,target,4);
delayedSizeA.Step(.294f,eye,true);delayedSizeB.Step(.294f,eye,true);
Check(delayedSizeA.LastFireDecision==delayedSizeB.LastFireDecision,"future target-size metadata cannot bypass sensory delay");
delayedSizeA.Step(.30f,eye,true);delayedSizeB.Step(.30f,eye,true);
Check(delayedSizeA.LastFireDecision.ToleranceDegrees>delayedSizeB.LastFireDecision.ToleranceDegrees,
 "target-size metadata becomes available with its delayed position sample");
Console.WriteLine($"{count} checks passed");
