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
  float now=.02f+profile.SensorDelay+.001f+tick/128f;
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
var withActual=HumanFireDecision.Evaluate(new(0,.3f),Vector2.Zero,.5f,0,true,.05f,false,new(AimWeaponClass.Rifle),.16f,2.0f);
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
var metadataTarget=new Vector3(1000,0,0);
for(int i=0;i<=25;i++)
{
 float now=i/128f;delayedSizeA.Observe(now,metadataTarget,4);delayedSizeB.Observe(now,metadataTarget,4);
 delayedSizeA.Step(now,eye,true);delayedSizeB.Step(now,eye,true);
}
delayedSizeA.Observe(.25f,metadataTarget,100);delayedSizeB.Observe(.25f,metadataTarget,4);
float beforeSize=.25f+profileHigh.SensorDelay-.001f,afterSize=.25f+profileHigh.SensorDelay+.001f;
delayedSizeA.Step(beforeSize,eye,true);delayedSizeB.Step(beforeSize,eye,true);
Check(delayedSizeA.LastFireDecision==delayedSizeB.LastFireDecision,"future target-size metadata cannot bypass sensory delay");
delayedSizeA.Step(afterSize,eye,true);delayedSizeB.Step(afterSize,eye,true);
Check(delayedSizeA.LastFireDecision.ToleranceDegrees>delayedSizeB.LastFireDecision.ToleranceDegrees,
 "target-size metadata becomes available with its delayed position sample");
foreach(var level in new[]{"Low","Medium","High"})
{
 var profile=HumanAimProfile.For(level);
 var once=new HumanAimModel(profile,Vector2.Zero,0,17);
 var repeated=new HumanAimModel(profile,Vector2.Zero,0,17);
 float maximumDifference=0;
 for(int i=0;i<512;i++)
 {
  float now=i/128f;var samplePosition=new Vector3(600,now*200,0);
  once.ObserveVisual(now,eye,samplePosition,Vector2.Zero,4);
  repeated.ObserveVisual(now,eye,samplePosition,Vector2.Zero,4);
  var a=once.Step(now,eye,true);var b=repeated.Step(now,eye,true);
  for(int extra=0;extra<3;extra++)b=repeated.Step(now,eye,true);
  maximumDifference=Math.Max(maximumDifference,AimMath.Delta(a.Angles,b.Angles).Length());
  if(a.AllowFire!=b.AllowFire)throw new Exception("duplicate calls changed fire permission");
 }
 Check(maximumDifference==0,level+" duplicate same-clock callbacks leave trajectory and firing unchanged");
}
var partProfile=profileHigh with {SensorDelay=0,SampleInterval=0,Prediction=1};
var parts=new HumanAimModel(partProfile,Vector2.Zero,0,42);
parts.Observe(.1f,new(600,0,64),4,part:"head");parts.Step(.1f,eye,true);
parts.Observe(.2f,new(600,0,48),8,part:"chest");parts.Step(.2f,eye,true);
parts.Step(.23f,eye,true);
Check(AimMath.Delta(parts.LastDesiredAngles,AimMath.Angles(eye,new(600,0,48))).Length()<.0001f,
 "head-to-chest selection does not become fictitious target velocity");
var cadence=new BurstCadence();
for(int shot=0;shot<4;shot++)cadence.Shot(shot*.08f,4,.32f);
Check(cadence.Paused(.3f) && !cadence.Paused(.57f),"burst pause has a finite deadline");
var pauseDeadline=cadence.PauseUntil;cadence.Shot(.32f,4,.32f);
Check(cadence.PauseUntil==pauseDeadline,"queued shot cannot continually extend a burst pause");
cadence.Shot(.57f,4,.32f);
Check(cadence.Shots==1 && !cadence.Paused(.57f),"new burst resets shot count after a pause");
var firstCue=new HeardSound(1,"footstep",new(-600,0,0),600,0,.25f,.95f,new(.2f,1));
var repeatedCue=SoundAttention.Merge(firstCue,firstCue with {HeardAt=.2f,Start=.45f,Until=1.15f,Error=new(3,4)});
Check(repeatedCue.Start==.25f,"continuous footsteps do not restart the pending reaction timer");
Check(repeatedCue.Error==firstCue.Error,"same source keeps a coherent direction error");
var closeGun=new HeardSound(2,"gunfire",new(300,0,0),300,0,.1f,.95f,Vector2.Zero);
var farStep=firstCue with {SourcePawn=3,Distance=1100,HeardAt=.1f,Start=.3f};
Check(ReferenceEquals(SoundAttention.Merge(closeGun,farStep),closeGun),"distant footstep does not replace a fresh close gunshot");
var farGun=closeGun with {Distance=2500};
var closeStep=firstCue with {Distance=100,HeardAt=.1f};
Check(SoundAttention.Merge(farGun,closeStep).SourcePawn==1,"near footstep can outrank a distant gunshot");
Check(MathF.Abs(AimMath.Angles(eye,firstCue.Position).Y)==180,"auditory direction retains sources behind the view cone");
var freshCue=firstCue with {HeardAt=2,Start=2.25f,Until=2.95f};
Check(SoundAttention.Merge(firstCue,freshCue).Start==2.25f,"expired sound memory starts a fresh reaction");
var execution=new AimExecutionController();
var request=execution.Update(0,new(0,10),Vector2.Zero,true);
Check(request.Correction.Y>0&&request.Correction.Y<=1,"execution feedback requests a bounded correction toward the motor plan");
Check(execution.Update(0,new(0,20),new(0,5),true)==request,"same-clock execution callbacks are idempotent");
float physicalYaw=0,maxCorrection=0;
var delayedCommands=new Queue<float>(Enumerable.Repeat(0f,4));
for(int i=1;i<=256;i++)
{
 float now=i/64f;var command=execution.Update(now,new(0,10),new(0,physicalYaw),true);
 maxCorrection=Math.Max(maxCorrection,Math.Abs(command.Correction.Y));
 delayedCommands.Enqueue(command.Angles.Y);
 physicalYaw+=(delayedCommands.Dequeue()-physicalYaw)*.18f;
}
Check(Math.Abs(physicalYaw-10)<.1f&&maxCorrection<=1.0001f,"bounded feedback converges through a delayed native-view plant");
var corrected=execution.Update(4.1f,new(0,10),Vector2.Zero,true);
var recoilOwnership=execution.Update(4.12f,new(0,10),Vector2.Zero,false);
Check(recoilOwnership.Correction.Y<corrected.Correction.Y&&!recoilOwnership.FeedbackActive,
 "feedback yields smoothly to native recoil during a burst");
var readiness=new AimExecutionController();
Check(!readiness.FirstShot(0,true,2,.65f).Allow,"first shot waits when the real view has not reached the motor plan");
Check(readiness.FirstShot(.05f,true,.2f,.65f).Reason=="execution-aligned","real alignment permits first shot immediately");
readiness.Shot();
Check(!readiness.FirstShot(1,true,2,.65f).Allow&&readiness.FirstShot(1.25f,true,2,.65f).Reason=="execution-settle-timeout",
 "nonconverging native execution has a bounded fallback instead of permanent silence");
var diagnosticGate=new AimExecutionController();
diagnosticGate.FirstShot(0,true,2,.65f,false);
Check(!diagnosticGate.FirstShot(1,true,2,.65f).Allow,"diagnostic evaluation cannot start the execution-wait timer");
Check(readiness.FirstShot(2,false,8,.65f).Allow,"live burst leaves recoil and continuing trigger to the native controller");
readiness.FirstShot(3,true,2,.65f);
readiness.ResetReadiness();
Check(!readiness.FirstShot(4,true,2,.65f).Allow,"lost visual plan cannot reuse an old execution timeout after reacquisition");
var ordinaryContext=new TacticalMovementContext(false,false,false,false,false,70,false,0,0,10);
Check(TacticalMovementPolicy.Evaluate(ordinaryContext).Quiet,"ordinary approach permits context-dependent quiet movement");
Check(!TacticalMovementPolicy.Evaluate(ordinaryContext with {BusyObjective=true}).Quiet,
 "planting or defusing cancels extra quiet movement");
Check(!TacticalMovementPolicy.Evaluate(ordinaryContext with {Frozen=true}).Peek,"freeze time cannot start a clearance peek");
Check(!TacticalMovementPolicy.Evaluate(ordinaryContext with {Combat=true}).Peek,"combat cancels clearance movement");
Check(TacticalMovementPolicy.Evaluate(ordinaryContext with {CarryingBomb=true,RoundSeconds=10}).Reason=="urgent-plant",
 "late bomb carrier prioritizes reaching the plant");
var retake=ordinaryContext with {CounterTerrorist=true,BombPlanted=true,BombSeconds=16,BombDistance=400};
Check(!TacticalMovementPolicy.Evaluate(retake).Quiet&&TacticalMovementPolicy.Evaluate(retake with {DefuseSeconds=5}).Quiet,
 "remaining travel and defuse-kit time change retake urgency");
Check(!TacticalMovementPolicy.Evaluate(ordinaryContext with {BombPlanted=true,BombSeconds=6,BombDistance=600}).Quiet,
 "nearby imminent bomb explosion prevents slow optional movement");
Check(!TacticalMovementPolicy.Evaluate(ordinaryContext with {RoundSeconds=float.NaN}).Peek,
 "unknown task timing fails closed for optional movement");
var safePeek=new PeekCandidate(new(0,28,0),true,true,true);
var localRoute=CoverPeekPolicy.Plan(3,Vector3.Zero,new(600,0,0),new(300,200,64),true,true,[safePeek]);
Check(localRoute.HasValue,"covered angle and safe revealing step create a local clearance route");
Check(CoverPeekPolicy.Plan(3,Vector3.Zero,new(600,0,0),new(300,200,64),false,true,[safePeek])==null,
 "open angle does not trigger pointless strafing");
Check(CoverPeekPolicy.Plan(3,Vector3.Zero,new(600,0,0),new(300,200,64),true,true,[safePeek with {GroundSafe=false}])==null,
 "ledge or missing footing rejects a peek");
Check(CoverPeekPolicy.Plan(3,Vector3.Zero,new(600,0,0),new(300,200,64),true,true,[safePeek with {PathClear=false}])==null,
 "blocked body corridor rejects a peek");
Check(CoverPeekPolicy.Plan(3,Vector3.Zero,new(600,0,0),new(300,200,64),true,true,[safePeek with {Destination=new(-24,28,0)}])==null,
 "peek cannot backtrack against the native route");
Check(CoverPeekPolicy.Cancel(localRoute!.Value,4,Vector3.Zero,new(600,0,0),true,.05f),"native route segment change cancels the old peek");
Check(CoverPeekPolicy.Cancel(localRoute.Value,3,Vector3.Zero,new(600,0,0),false,.05f),"task priority can cancel an in-flight peek");
Check(CoverPeekPolicy.Cancel(localRoute.Value,3,Vector3.Zero,new(600,0,0),true,.2f),"peek has a hard finite duration");
Check(CoverPeekPolicy.Cancel(localRoute.Value,3,new(float.NaN,0,0),new(600,0,0),true,.05f),"invalid native position cancels optional movement");
var previousProfiles=new Dictionary<string,HumanAimProfile>{
 ["Low"]=new(.200f,.090f,.050f,.155f,.32f,.020f,.105f,480,.60f){PerceptionError=.12f,BurstLength=4,BurstPause=.32f},
 ["Medium"]=new(.170f,.065f,.035f,.125f,.16f,.011f,.070f,650,.80f),
 ["High"]=new(.130f,.045f,.025f,.095f,.07f,.006f,.045f,850,.92f){PerceptionError=.035f,BurstLength=8,BurstPause=.24f}
};
foreach(float gap in new[]{5f,25f,70f})
{
 float lastMedian=float.PositiveInfinity;
 var point=new Vector3(1000*MathF.Cos(gap*MathF.PI/180),1000*MathF.Sin(gap*MathF.PI/180),0);
 foreach(string level in new[]{"Low","Medium","High"})
 {
  var profile=HumanAimProfile.For(level);
  var oldTimes=Enumerable.Range(0,128).Select(seed=>FirstFire(previousProfiles[level],point,AimWeaponClass.Rifle,seed).Time).Order().ToArray();
  var newTimes=Enumerable.Range(0,128).Select(seed=>FirstFire(profile,point,AimWeaponClass.Rifle,seed).Time).Order().ToArray();
  float oldMedian=oldTimes[64],newMedian=newTimes[64];
  Check(newMedian<oldMedian,$"{level} {gap}deg contact reaches first planned shot faster than 8.34");
  Check(newMedian<lastMedian,$"{level} {gap}deg contact preserves difficulty ordering");
  lastMedian=newMedian;
  Console.WriteLine($"TUNING {level} gap={gap} old_median_ms={oldMedian*1000:F1} new_median_ms={newMedian*1000:F1}");
 }
}
var allyProfile=CombatBalancePolicy.AllyProfile(HumanAimProfile.For("Medium"),.75f);
Check(MathF.Abs(allyProfile.MaxSpeed/HumanAimProfile.For("Medium").MaxSpeed-.75f)<.00001f,
 "human ally motor speed has the requested twenty-five percent parameter handicap");
Check(CombatBalancePolicy.NormalizeAllyStrength(.75f)==.75f,
 "configuration accepts the requested 0.75 strength without clamping it back to 0.8");
Check(CombatBalancePolicy.NormalizeAllyStrength(.85f)==.85f&&CombatBalancePolicy.NormalizeAllyStrength(float.NaN)==.75f,
 "legacy explicit strength remains valid and invalid settings use the new default");
Check(allyProfile.Reaction>HumanAimProfile.For("Medium").Reaction&&allyProfile.MotorError>HumanAimProfile.For("Medium").MotorError,
 "ally handicap slows response and slightly reduces precision");
Check(CombatBalancePolicy.IsHumanAlly(3,1<<3)&&!CombatBalancePolicy.IsHumanAlly(2,1<<3),
 "handicap follows the human team and never the opponent");
Check(CombatBalancePolicy.IsHumanAlly(2,1<<2)&&!CombatBalancePolicy.IsHumanAlly(3,1<<2),
 "team switch reverses the handicap without assuming CT is always friendly");
Check(!CombatBalancePolicy.IsHumanAlly(3,0)&&!CombatBalancePolicy.IsHumanAlly(2,(1<<2)|(1<<3)),
 "spectators and humans on both sides do not create one-sided handicaps");
Check(CombatBalancePolicy.AllyProfile(profileHigh,1)==profileHigh,"unit ally strength disables the handicap");
Check(CombatBalancePolicy.HeadIntentChance("Medium",AimWeaponClass.Rifle,"weapon_ak47")==.4f,
 "mixed rifles no longer always select a head-first intent");
Check(CombatBalancePolicy.HeadIntentChance("Medium",AimWeaponClass.Sniper,"weapon_awp")==0,
 "AWP retains body-first intent");
Check(CombatBalancePolicy.HeadIntentChance("Medium",AimWeaponClass.PrecisePistol,"weapon_deagle")>
 CombatBalancePolicy.HeadIntentChance("Medium",AimWeaponClass.Smg,"weapon_mp9"),
 "precision pistol and SMG do not share identical head preference");
var intents=new EngagementAimIntents();var intentKey=new AimIntentKey(1,2,3,0);int draws=0;
float IntentDraw(){draws++;return draws==1?.2f:.8f;}
bool intent=intents.Resolve(4,intentKey,.4f,0,IntentDraw);
for(int i=1;i<1000;i++)CheckIntent(intents.Resolve(4,intentKey,.4f,i/128f,IntentDraw)==intent);
void CheckIntent(bool ok){if(!ok)throw new Exception("aim intent changed within continuous engagement");}
Check(draws==1,"repeated native/model callbacks share one stable intent draw");
Check(!intents.Resolve(4,intentKey with{Target=5},.4f,8,IntentDraw)&&draws==2,
 "new enemy gets a new intent instead of inheriting the previous target's head choice");
intents.Resolve(4,intentKey with{Target=5},.4f,10,IntentDraw);
Check(draws==3,"lost contact longer than one second starts a fresh intent");
intents.Clear();Check(intents.Get(4)==null,"round reset clears engagement intents");
foreach(float gap in new[]{5f,25f,70f})
{
 var point=new Vector3(1000*MathF.Cos(gap*MathF.PI/180),1000*MathF.Sin(gap*MathF.PI/180),0);
 foreach(string level in new[]{"Low","Medium","High"})
 {
  var current=HumanAimProfile.For(level);
  var prior=current with{FlickTime=level switch{"Low"=>.125f,"High"=>.065f,_=>.085f},FlickAmplitudeTime=.018f,FirstShotSettleLimit=.24f};
  var beforeTimes=Enumerable.Range(0,128).Select(seed=>FirstFire(prior,point,AimWeaponClass.Rifle,seed).Time).Order().ToArray();
  var afterTimes=Enumerable.Range(0,128).Select(seed=>FirstFire(current,point,AimWeaponClass.Rifle,seed).Time).Order().ToArray();
  Check(afterTimes[64]<beforeTimes[64],$"{level} {gap}deg faster initial movement advances planned fire without reducing reaction");
  Console.WriteLine($"FLICK-TUNING {level} gap={gap} old_median_ms={beforeTimes[64]*1000:F1} new_median_ms={afterTimes[64]*1000:F1}");
 }
}
foreach(string level in new[]{"Low","Medium","High"})
{
 var profile=HumanAimProfile.For(level);var gate=new AimExecutionController();
 Check(gate.FirstShot(0,true,.1f,.65f,true,profile.FirstShotSettleLimit).Allow,
  level+" aligned shot never pays the maximum execution settling time");
 gate.FirstShot(1,true,2,.65f,true,profile.FirstShotSettleLimit);
 Check(!gate.FirstShot(1+profile.FirstShotSettleLimit-.005f,true,2,.65f,true,profile.FirstShotSettleLimit).Allow,
  level+" unaligned native execution still receives its bounded settling attempt");
 Check(gate.FirstShot(1+profile.FirstShotSettleLimit+.005f,true,2,.65f,true,profile.FirstShotSettleLimit).Reason=="execution-settle-timeout",
  level+" failed native alignment yields by the new per-difficulty deadline");
}
var balanceRoster=new[]{new BalanceBot(100,2),new BalanceBot(101,2),new BalanceBot(102,2),new BalanceBot(103,2),new BalanceBot(104,2),new BalanceBot(200,3)};
var eliteRoster=new EnemyEliteRoster();
Check(eliteRoster.Refresh(8,balanceRoster,new Random(42)),"single human team initializes the opposing elite roster");
Check(eliteRoster.Strengths.Count is 1 or 2&&eliteRoster.Strengths.Keys.All(id=>id<200),"one or two distinct opponents are elites, never human teammates");
Check(eliteRoster.Strengths.Values.All(s=>s>=1.20f&&s<=1.30f),"elite strengths stay within twenty to thirty percent");
var selectedElites=eliteRoster.Strengths.ToDictionary(x=>x.Key,x=>x.Value);
Check(!eliteRoster.Refresh(8,balanceRoster.Reverse(),new Random(99))&&selectedElites.SequenceEqual(eliteRoster.Strengths),"repeated combat updates and roster order do not reroll elites");
Check(eliteRoster.Strength(999)==1,"a reused player slot with a new entity serial does not inherit elite strength");
Check(eliteRoster.Refresh(4,balanceRoster,new Random(42))&&eliteRoster.Strengths.Count==1&&eliteRoster.Strength(200)>1,
 "switching human team moves elites to the new enemy side");
eliteRoster.Refresh(0,balanceRoster,new Random(1));Check(eliteRoster.Strengths.Count==0,"spectators do not leave enemy elites active");
eliteRoster.Refresh(12,balanceRoster,new Random(1));Check(eliteRoster.Strengths.Count==0,"humans on both sides disable asymmetric elites");
eliteRoster.Reset();Check(eliteRoster.Strengths.Count==0&&eliteRoster.Refresh(8,balanceRoster,new Random(2)),"new round can select a fresh fixed roster");
bool sawOne=false,sawTwo=false;
for(int seed=0;seed<32;seed++){var r=new EnemyEliteRoster();r.Refresh(8,balanceRoster,new Random(seed));sawOne|=r.Strengths.Count==1;sawTwo|=r.Strengths.Count==2;}
Check(sawOne&&sawTwo,"round selection supports both one and two elite opponents");
var eliteProfile=CombatBalancePolicy.AllyProfile(HumanAimProfile.For("Medium"),1.25f);
Check(MathF.Abs(eliteProfile.Reaction-(.096f/1.10f))<.00001f&&eliteProfile.MotorError<HumanAimProfile.For("Medium").MotorError,
 "elite boost accelerates response and improves precision without damage changes");
var midBefore840=new HumanAimProfile(.140f,.045f,.025f,.060f,.10f,.0075f,.045f,780,.90f)
 {PerceptionError=.05f,FlickAmplitudeTime=.008f,FirstShotSettleLimit=.10f,BurstLength=8,BurstPause=.18f};
foreach(float gap in new[]{5f,25f,70f})
{
 var point=new Vector3(1000*MathF.Cos(gap*MathF.PI/180),1000*MathF.Sin(gap*MathF.PI/180),0);
 var oldTimes=Enumerable.Range(0,128).Select(seed=>FirstFire(midBefore840,point,AimWeaponClass.Rifle,seed).Time).Order().ToArray();
 var newTimes=Enumerable.Range(0,128).Select(seed=>FirstFire(HumanAimProfile.For("Medium"),point,AimWeaponClass.Rifle,seed).Time).Order().ToArray();
 Check(newTimes[64]<oldTimes[64],$"8.40 Mid {gap}deg contact advances planned first fire relative to 8.39");
 Console.WriteLine($"MID-840 gap={gap} old_median_ms={oldTimes[64]*1000:F1} new_median_ms={newTimes[64]*1000:F1}");
}
Check(MathF.Abs(HumanAimProfile.For("Medium").MotorError-midBefore840.MotorError/1.10f)<.00001f
 &&MathF.Abs(HumanAimProfile.For("Medium").PerceptionError-midBefore840.PerceptionError/1.10f)<.00001f,"global combat scaling reduces Mid motor and sensory errors by the common factor");
var before842=new Dictionary<string,HumanAimProfile>{
 ["Low"]=new(.180f,.075f,.040f,.105f,.24f,.015f,.085f,550,.72f){PerceptionError=.095f,FlickAmplitudeTime=.011f,FirstShotSettleLimit=.16f,BurstLength=5,BurstPause=.26f},
 ["Medium"]=new(.120f,.035f,.020f,.045f,.10f,.0075f,.032f,850,.90f){PerceptionError=.05f,FlickAmplitudeTime=.008f,FirstShotSettleLimit=.10f,BurstLength=8,BurstPause=.15f},
 ["High"]=new(.110f,.030f,.016f,.045f,.045f,.0035f,.030f,950,.97f){PerceptionError=.025f,FlickAmplitudeTime=.006f,FirstShotSettleLimit=.08f,BurstLength=10,BurstPause=.14f}
};
foreach(var (level,prior) in before842)
{
 var current=HumanAimProfile.For(level);
 Check(Math.Abs(current.MaxSpeed/prior.MaxSpeed-1.10f)<.00001f&&Math.Abs(current.Reaction*1.10f-prior.Reaction)<.00001f,
  level+" global scale increases speed and reduces latency consistently");
 Check(Math.Abs(current.MotorError*1.10f-prior.MotorError)<.00001f&&Math.Abs(current.PerceptionError*1.10f-prior.PerceptionError)<.00001f,
  level+" global scale improves precision without changing target preference");
 Check(Math.Abs(current.SampleInterval*1.10f-prior.SampleInterval)<.00001f&&current.BurstLength==prior.BurstLength,
  level+" faster sampling keeps discrete burst lengths unchanged");
 Check(Math.Abs(CombatBalancePolicy.AllyProfile(current,.85f).MaxSpeed/current.MaxSpeed-.85f)<.00001f,
  level+" overall boost preserves relative ally handicap");
}
var preparedProfile=HumanAimProfile.For("Medium") with{MotorError=0,AmplitudeError=0};
var onset=new HumanAimModel(preparedProfile,Vector2.Zero,0,42);
onset.Observe(0,new(600,0,0),4);onset.Step(.04f,Vector3.Zero,true);
onset.Observe(.05f,new(600,25,0),4);
var onsetOutput=onset.Step(.085f,Vector3.Zero,true);
Check(onsetOutput.Phase=="reaction"&&onset.MovementEndpoint.Y>2,
 "reaction updates the pending endpoint from arrived samples instead of freezing its first position");
var committed=new HumanAimModel(HumanAimProfile.For("Medium"),Vector2.Zero,0,4);
committed.Observe(0,new(600,0,0));committed.Step(.01f,Vector3.Zero,true);
committed.CommitShot(.01f,Vector2.Zero);
var afterCommit=committed.Step(.05f,Vector3.Zero,true);
Check(afterCommit.Phase is not ("reaction" or "flick"),"an already-fired contact cannot restart initial reaction after perception delay");
var hiddenCommit=new HumanAimModel(HumanAimProfile.For("Medium"),Vector2.Zero,0,4);
hiddenCommit.CommitShot(0,Vector2.Zero);
Check(!hiddenCommit.Step(.1f,Vector3.Zero,true).AllowFire,"shot continuity never invents an enemy observation");
foreach(float gap in new[]{0f,10f})
{
 var movingFireTimes=new List<float>();
 for(int seed=0;seed<32;seed++)
 {
  var linear=new HumanAimModel(HumanAimProfile.For("Medium"),Vector2.Zero,0,seed);
  for(int tick=0;tick<128;tick++)
  {
   float t=tick/128f;linear.Observe(t,new(600,600*MathF.Tan(gap*MathF.PI/180)+250*t,0),4);
   if(linear.Step(t,Vector3.Zero,true,new(AimWeaponClass.Rifle)).AllowFire){movingFireTimes.Add(t);break;}
  }
 }
 movingFireTimes.Sort();
 Check(movingFireTimes.Count==32&&movingFireTimes[16]<(gap==0?.18f:.24f),
  $"Mid {gap}deg linear moving contact no longer requires the stale endpoint correction cycle");
}
var noticed=new VisibleTargetHistory();var cp=HumanAimProfile.For("Medium");
noticed.Notice(42,1);noticed.Notice(42,1.1f);
Check(noticed.ReactionCredit(42,1.12f,cp)>0&&noticed.ReactionCredit(42,1.12f,cp)<=cp.Reaction*.75f,
 "continuously seen alternative receives bounded reaction credit");
Check(noticed.ReactionCredit(43,1.12f,cp)==0&&noticed.ReactionCredit(42,1.4f,cp)==0,
 "hidden or expired alternatives receive no reaction credit");
noticed.Notice(42,1.5f);
Check(noticed.ReactionCredit(42,1.5f,cp)==0,"a newly reappearing target has no accumulated stale visibility credit");
var creditModel=new HumanAimModel(cp,Vector2.Zero,0,42,999);
Check(creditModel.ReactionCredit<=cp.Reaction*.75f,"target transfer cannot bypass the entire reaction period");
var shooterHistory=new CombatHistory();shooterHistory.Shot(1,42,2,.2f);shooterHistory.Shot(1.1f,43,2,.2f);
Check(shooterHistory.SprayShots==2&&shooterHistory.LastTarget==43&&shooterHistory.BurstCooldownUntil>1.1f,
 "switching enemy preserves the shooter's actual burst cadence and pause");
Check(TeamSupportPolicy.Candidate(new(300,0,0),Vector3.Zero,true)
 &&!TeamSupportPolicy.Candidate(new(300,0,150),Vector3.Zero,true)
 &&!TeamSupportPolicy.Candidate(new(300,0,0),Vector3.Zero,false),"support excludes another floor and unavailable teammates");
Check(!TeamSupportPolicy.Candidate(new(1000,0,0),Vector3.Zero,true)
 &&!TeamSupportPolicy.Candidate(new(30,0,0),Vector3.Zero,true),"support neither crosses the map nor stacks an already adjacent teammate");
Check(TeamSupportPolicy.Route(500,400)&&!TeamSupportPolicy.Route(1200,400)&&!TeamSupportPolicy.Route(900,200),
 "support requires a bounded reachable route instead of a long detour");
Check(TeamSupportPolicy.Continue(1.5f,1,1.4f,200,true)
 &&!TeamSupportPolicy.Continue(4,1,4,200,true)&&!TeamSupportPolicy.Continue(2,1,1,200,true)
 &&!TeamSupportPolicy.Continue(1.5f,1,1.4f,50,true)&&!TeamSupportPolicy.Continue(1.5f,1,1.4f,200,false),
 "support ends on deadline, lack of progress, arrival or loss of availability");
var grossGate=new AimExecutionController();
var alertCredit=new HumanAimModel(HumanAimProfile.For("Medium"),Vector2.Zero,0,2);
alertCredit.CreditAlertReaction(1);
Check(alertCredit.ReactionCredit<=HumanAimProfile.For("Medium").Reaction*.75f&&alertCredit.ReactionCredit>0,
 "already-ready sound attention grants bounded credit without skipping full perception");
var physicalArrival=new HumanAimModel(HumanAimProfile.For("Medium"),Vector2.Zero,0,2);
physicalArrival.Observe(0,new(0,600,0));
var beforeArrival=physicalArrival.Step(.06f,Vector3.Zero,true,default,Vector2.Zero);
Check(beforeArrival.Phase=="reaction"&&!beforeArrival.AllowFire,"large-turn assistance cannot skip the initial reaction deadline");
for(int i=1;i<=24;i++)
{
 float t=.06f+i*.01f;physicalArrival.Observe(t,new(0,600,0));
 beforeArrival=physicalArrival.Step(t,Vector3.Zero,true,default,new(0,t>.22f?90:0));
 if(t>.22f)break;
}
Check(beforeArrival.Phase=="tracking"&&beforeArrival.AllowFire,
 "actual aligned execution finishes the motor arc instead of waiting for the old virtual cursor");
var anchorModel=new HumanAimModel(HumanAimProfile.For("Medium"),new(0,120),0,7);
anchorModel.Observe(0,new(600,0,0));
Check(anchorModel.Step(.01f,Vector3.Zero,true,default,new(0,60)).Angles.Y==60,
 "pending perception follows actual sound-driven orientation instead of preserving an obsolete birth yaw");
Check(anchorModel.Step(.05f,Vector3.Zero,true,default,new(0,40)).Angles.Y==40,
 "reaction cannot pull native orientation back after an acoustic turn");
Check(!grossGate.FirstShot(0,true,90,.65f,true,.1f,90).Allow
 &&!grossGate.FirstShot(2,true,90,.65f,true,.1f,90).Allow,
 "large real turn error never becomes a timeout-authorized sideways first shot");
Check(!grossGate.FirstShot(3,false,90,.65f,true,.1f,90).Allow,
 "a burst continuation cannot bypass a large horizontal execution error");
Check(grossGate.FirstShot(3.1f,true,.1f,.65f,true,.1f,.1f).Allow,
 "turn safety releases immediately after actual view alignment");
Check(FacingFirePolicy.OutsideCone(0,90)&&FacingFirePolicy.OutsideCone(0,180)
 &&!FacingFirePolicy.OutsideCone(179,-179)&&!FacingFirePolicy.OutsideCone(0,8),
 "native fallback fire cone blocks rear and side targets with correct yaw wrapping");
foreach(var level in new[]{"Low","Medium","High"})
{
 var p=HumanAimProfile.For(level);
 Check(MathF.Abs(HumanAimModel.TurnSpeed(p.MaxSpeed,15)/p.MaxSpeed-1.20f)<.00001f
  &&HumanAimModel.TurnSpeed(p.MaxSpeed,180)<=p.MaxSpeed*1.651f*1.20f,
  level+" initial positioning and bounded large turns receive the twenty percent turn-rate increase");
 foreach(float degrees in new[]{90f,180f})
 {
  var turn=new HumanAimModel(p,Vector2.Zero,0,3);
  turn.Observe(0,new(600*MathF.Cos(degrees*MathF.PI/180),600*MathF.Sin(degrees*MathF.PI/180),0));
  turn.Step(.08f,Vector3.Zero,true);
  Check(turn.PlanDuration<1.875f*degrees/p.MaxSpeed*.9f,
   $"{level} {degrees}deg planned turn no longer uses the slow small-correction speed cap");
 }
}
foreach(var level in new[]{"Low","Medium","High"})
 foreach(float gap in new[]{5f,90f,180f})
 {
  var p=HumanAimProfile.For(level);float width=MathF.Max(.12f,MathF.Atan2(8,600)*180/MathF.PI);
  float oldSpeed=p.MaxSpeed*(1+.65f*Math.Clamp((gap-25)/95,0,1));
  float oldDuration=MathF.Max(gap<width*.5f?.015f:p.FlickTime+p.FlickAmplitudeTime*MathF.Log2(1+gap/width),1.875f*gap/oldSpeed);
  var turn=new HumanAimModel(p,Vector2.Zero,0,3);turn.Observe(0,new(600*MathF.Cos(gap*MathF.PI/180),600*MathF.Sin(gap*MathF.PI/180),0));
  turn.Step(.08f,Vector3.Zero,true);
  Check(MathF.Abs(HumanAimModel.TurnSpeed(p.MaxSpeed,gap)/oldSpeed-1.20f)<.00001f
   &&MathF.Abs(turn.PlanDuration*1.20f-oldDuration)<.0001f,
   $"{level} {gap}deg movement rate scales once and motor duration divides by 1.2");
 }
var otherSource=new HeardSound(99,"gunfire",new(100,0,0),100,.3f,.55f,1.2f,Vector2.Zero);
Check(SoundAttention.Merge(firstCue,otherSource).Start==.55f,
 "a newly selected sound source cannot borrow the previous source's reaction deadline");
var promoted=SoundAttention.Merge(firstCue,firstCue with{Source="gunfire",HeardAt=.1f,Start=.12f,Until=1.1f,Error=new(3,4)});
Check(promoted.Gunshot&&promoted.Error==firstCue.Error&&promoted.Start==.12f,
 "same-source footstep to gunfire promotion keeps coherent direction error and earliest reaction");
var focus=new SoundGazeFocus();
var brief=firstCue with{Until=.5f};focus.Select(brief,0);
Check(focus.Holding(.2f)&&!focus.Ready(.2f)&&focus.Ready(.26f),
 "pending sound reaction reserves gaze without starting an early turn");
Check(focus.Holding(.8f),"a brief footstep has enough focus time to finish turning and glance before returning to navigation");
focus.Select(brief with{HeardAt=.2f,Start=.45f,Until=.7f},.2f);
Check(focus.ReadyAt==.25f,"repeated source refresh does not restart a gaze reaction");
focus.Select(brief with{HeardAt=.7f,Start=.95f,Until=1.9f},.7f);
Check(focus.Until<=1.6f,"continuous sounds cannot hold the gaze indefinitely");
focus.End(1.6f);
Check(!focus.Select(brief with{HeardAt=1.7f,Start=1.9f,Until=2.2f},1.7f),
 "a completed source check cannot immediately pull the view away from navigation again");
Check(focus.Select(brief with{Source="gunfire",HeardAt=1.8f,Start=1.9f,Until=2.7f},1.8f),
 "a genuine escalation from footsteps to gunfire can interrupt the same-source recheck cooldown");
foreach(var level in new[]{"Low","Medium","High"})
{
 var original=HumanAimProfile.For(level);
 var weakened=CombatBalancePolicy.EnemyProfile(original,.8f);
 Check(MathF.Abs(weakened.MaxSpeed/original.MaxSpeed-.8f)<.00001f,level+" enemy motor strength reduced by twenty percent");
 Check(MathF.Abs(weakened.Reaction/original.Reaction-1.25f)<.00001f&&MathF.Abs(weakened.MotorError/original.MotorError-1.25f)<.00001f,
  level+" weaker enemies have coherently slower reaction and less precision");
 foreach(float elite in new[]{1.2f,1.25f,1.3f})
 {
  var oldElite=CombatBalancePolicy.AllyProfile(original,elite);
  var newElite=CombatBalancePolicy.AllyProfile(weakened,elite);
  Check(MathF.Abs(newElite.MaxSpeed/oldElite.MaxSpeed-.8f)<.00001f&&MathF.Abs(newElite.MaxSpeed/weakened.MaxSpeed-elite)<.00001f,
   level+" elite also loses twenty percent while retaining its relative advantage");
 }
 Check(CombatBalancePolicy.AllyProfile(original,.75f)==original.ScaleCombat(.75f),level+" teammate profile unchanged");
}
Check(CombatBalancePolicy.IsHumanEnemy(2,8)&&CombatBalancePolicy.IsHumanEnemy(3,4),"enemy reduction follows humans after side switch");
Check(!CombatBalancePolicy.IsHumanEnemy(3,8)&&!CombatBalancePolicy.IsHumanEnemy(2,4),"enemy reduction never applies to teammates");
Check(!CombatBalancePolicy.IsHumanEnemy(2,0)&&!CombatBalancePolicy.IsHumanEnemy(3,12),"spectators and humans on both sides stay neutral");
Check(CombatBalancePolicy.NormalizeEnemyStrength(float.NaN)==.8f&&CombatBalancePolicy.NormalizeEnemyStrength(0)>.0f,"invalid enemy scale cannot cause invalid division");
Console.WriteLine($"{count} checks passed");
