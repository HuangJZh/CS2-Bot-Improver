using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using BotControllerApi;
using System.Text.Json;
public sealed class HumanAimProbe:BasePlugin
{
 private readonly List<(int Slot,long Token,string Kind)> _controls=new();
 private IBotControllerApi? _api;
 private int _budgetActor=-1,_budgetCount;
 private uint _budgetPawn;
 private long _budgetToken;
 private int _recordSlot=-1;
 private uint _recordPawn;
 private float _recordUntil;
 private readonly List<object> _recordClock=new();
 private readonly Dictionary<int,uint> _primaryLocks=new();
 private CounterStrikeSharp.API.Modules.Timers.Timer? _forcedPerception;
 private MemoryFunctionVoid<nint,nint>? _setEyeAngles;
 private MemoryFunctionVoid<nint>? _lookHook;
 private readonly Dictionary<nint,(int Slot,uint Pawn,float Until,float Yaw)> _facePoses=new();
 private CCSGameRules? FixtureRules()=>Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault(p=>p.IsValid)?.GameRules;
 // Long-range fixture helpers: find a ground point and verify a clear eye-to-chest
 // line. The pair -1076,-843 / -1015,-808 comes from a user-provided Dust2 demo;
 // the search only accepts points that are both on the world brush and visible.
 private static bool TryFindGround(float x,float y,float zHint,out float z)
 {
  z=0;
  var start=new Vector(x,y,zHint+250f);var end=new Vector(x,y,zHint-450f);
  var trace=Trace.TraceEndShape(start,end,options:new TraceOptions{InteractsWith=Masks.SolidBrushOnly});
  float f=trace.Fraction;
  if(!(f>.02f && f<.98f))return false;
  z=start.Z+(end.Z-start.Z)*f;
  return true;
 }
 private bool TryPlaceRange(float distance,CCSPlayerController actor,CCSPlayerController target)
 {
  if(actor.PlayerPawn.Value is not {IsValid:true} ap || target.PlayerPawn.Value is not {IsValid:true} tp)return false;
  var origin=new Vector(-1076f,-843f,116.789474f);
  ap.Teleport(origin,new QAngle(0,29.85f,0),new Vector(0,0,0));
  var eye=new Vector(origin.X,origin.Y,origin.Z+(ap.ViewOffset?.Z ?? 64f));
  float baseAngle=MathF.Atan2(0.497f,0.867f);
  float[] offsets={0f,8f,-8f,16f,-16f,24f,-24f,32f,-32f,40f,-40f,60f,-60f,90f,-90f,120f,-120f,150f,-150f,180f};
  foreach(float off in offsets)
  {
   float rad=baseAngle+off*MathF.PI/180f;
   float x=origin.X+MathF.Cos(rad)*distance;
   float y=origin.Y+MathF.Sin(rad)*distance;
   if(!TryFindGround(x,y,origin.Z,out float z))continue;
   float targetEye=tp.ViewOffset?.Z ?? 64f;
   var chest=new Vector(x,y,z+targetEye*.8f);
   var head=new Vector(x,y,z+targetEye);
   try
   {
    var chestTrace=Trace.TraceEndShape(eye,chest,options:new TraceOptions{InteractsWith=Masks.SolidBrushOnly});
    if(chestTrace.Fraction<.995f)continue;
    var headTrace=Trace.TraceEndShape(eye,head,options:new TraceOptions{InteractsWith=Masks.SolidBrushOnly});
    if(headTrace.Fraction<.995f)continue;
    float yawToTarget=MathF.Atan2(y-origin.Y,x-origin.X)*180f/MathF.PI;
    tp.Teleport(new Vector(x,y,z),new QAngle(0,yawToTarget+180f,0),new Vector(0,0,0));
    try
    {
     uint flags=actor.Flags;
     if(OperatingSystem.IsWindows())actor.Flags=flags|0x100u;
     _setEyeAngles!.Invoke(ap.Handle,new QAngle(0,yawToTarget,0).Handle);
     if(OperatingSystem.IsWindows() && actor.IsValid)actor.Flags=flags;
    }
    catch(Exception) { }
    Server.PrintToConsole($"[PROBE] contact_range distance={distance:F0} offset={off:F0} target=({x:F1},{y:F1},{z:F1}) chest={chestTrace.Fraction:F3} head={headTrace.Fraction:F3}");
    File.AppendAllText(Path.Combine(ModuleDirectory,"fixture-events.jsonl"),JsonSerializer.Serialize(new {kind="scene_placed",scene="contact_range",distance,
      offset=off,tick=Server.TickCount,time=Server.CurrentTime,observer_slot=actor.Slot,target_slot=target.Slot,
      target_x=x,target_y=y,target_z=z,chest_fraction=chestTrace.Fraction,head_fraction=headTrace.Fraction,
      warmup=FixtureRules()?.WarmupPeriod,freeze=FixtureRules()?.FreezePeriod})+"\n");
    return true;
   }
   catch(Exception) { }
  }
  return false;
 }
 private HookResult OnFixtureLookPost(DynamicHook hook)
 {
  var pointer=hook.GetParam<nint>(0);
  if(!_facePoses.TryGetValue(pointer,out var pose))return HookResult.Continue;
  var player=Utilities.GetPlayerFromSlot(pose.Slot);
  if(Server.CurrentTime>=pose.Until || player is not {IsValid:true,PawnIsAlive:true} || player.PlayerPawn.Raw!=pose.Pawn)
  {_facePoses.Remove(pointer);return HookResult.Continue;}
  var pawn=player.PlayerPawn.Value!;
  if(pawn.Bot is {} bot && bot.IsEnemyVisible && bot.Enemy.Raw!=uint.MaxValue)
  {_facePoses.Remove(pointer);return HookResult.Continue;}
  var angles=new QAngle(0,pose.Yaw,0);uint flags=player.Flags;
  try{if(OperatingSystem.IsWindows())player.Flags=flags|0x100u;_setEyeAngles!.Invoke(pawn.Handle,angles.Handle);}
  finally{if(OperatingSystem.IsWindows() && player.IsValid)player.Flags=flags;}
  return HookResult.Continue;
 }
 private void StopMotionCapture()
 {
  if(_recordSlot<0 || _api==null)return;
  int slot=_recordSlot;_recordSlot=-1;
  _api.StopRecord(slot);
  var (ticks,subs,commands)=_api.GetRecordedMotionExtended(slot);
  var directory=Path.Combine(ModuleDirectory,"recordings");Directory.CreateDirectory(directory);
  var path=Path.Combine(directory,$"motion-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
  File.WriteAllText(path,JsonSerializer.Serialize(new {schema="probe-native-motion-v1",slot,pawn=_recordPawn,
   note="Recording only, no replay. Clock counts bracket native records; no exact subtick timestamp claim. Command Fields=0 may mean absent data.",
   clocks=_recordClock,ticks,subs,commands},new JsonSerializerOptions{IncludeFields=true}));
  _recordClock.Clear();Server.PrintToConsole($"[PROBE] native recording ticks={ticks.Length} commands={commands.Length} subs={subs.Length} path={path}");
 }
 private void ReleaseControls()
 {
  StopMotionCapture();_budgetToken=0;_budgetActor=-1;
  _forcedPerception?.Kill();_forcedPerception=null;
  _facePoses.Clear();
  foreach(var (slot,pawn) in _primaryLocks)
  {
   var player=Utilities.GetPlayerFromSlot(slot);
   if(player is {IsValid:true} && player.PlayerPawn.Raw==pawn && _api?.GetWeaponLock(slot)==LockTarget.Slot1)_api.Unlock(slot,LockKind.Weapon);
  }
  _primaryLocks.Clear();
  foreach(var c in _controls)try { if(c.Kind=="move")_api?.CancelUsercmdMovement(c.Slot,c.Token);else if(c.Kind=="suppress")_api?.CancelUsercmdSuppression(c.Slot,c.Token);else _api?.CancelUsercmdInjection(c.Slot,c.Token); }catch { }
  _controls.Clear();
 }
 public override void Unload(bool hotReload)
 {
  ReleaseControls();_lookHook?.Unhook(OnFixtureLookPost,HookMode.Post);
 }
 public override string ModuleName=>"HumanAimProbe";
 public override string ModuleVersion=>"5-live-contact-fixture";
 public override void Load(bool hotReload)
 {
  // Never run this fixture in a normal client. Deliberately constrained to our test server.
  var args=Environment.GetCommandLineArgs();
  int port=Array.IndexOf(args,"-port"),ip=Array.IndexOf(args,"-ip");
  if(!args.Contains("-dedicated") || port<0 || port+1>=args.Length || args[port+1]!="27025"
     || ip<0 || ip+1>=args.Length || args[ip+1]!="127.0.0.1")return;
  using(var data=JsonDocument.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(ModuleDirectory,"../../../../addons/BotController/gamedata.json")))))
  {
   string platform=OperatingSystem.IsWindows() ? "windows" : "linux";
   _setEyeAngles=new(data.RootElement.GetProperty("CCSPlayerPawn::SetEyeAngles").GetProperty("signatures").GetProperty(platform).GetString()!);
   _lookHook=new(data.RootElement.GetProperty("CCSBot::UpdateLookAngles").GetProperty("signatures").GetProperty(platform).GetString()!);
   if(_setEyeAngles.Handle==nint.Zero || _lookHook.Handle==nint.Zero)throw new InvalidOperationException("Fixture view signatures unavailable");
   _lookHook.Hook(OnFixtureLookPost,HookMode.Post);
  }
  RegisterListener<Listeners.OnMapEnd>(ReleaseControls);
  RegisterListener<Listeners.OnTick>(()=>{
   if(_recordSlot<0 || _api==null)return;
   var player=Utilities.GetPlayerFromSlot(_recordSlot);
   if(Server.CurrentTime>=_recordUntil || player is not {IsValid:true} || player.PlayerPawn.Raw!=_recordPawn){StopMotionCapture();return;}
   _recordClock.Add(new {kind="tick_count_bracket",tick=Server.TickCount,time=Server.CurrentTime,count=_api.RecordedTickCount(_recordSlot)});
  });
  RegisterEventHandler<EventWeaponFire>((e,i)=>{
   var player=e.Userid;
   if(_recordSlot>=0 && player is {IsValid:true} && player.Slot==_recordSlot)
    _recordClock.Add(new {kind="weapon_fire_bracket",tick=Server.TickCount,time=Server.CurrentTime,count=_api?.RecordedTickCount(_recordSlot),weapon=e.Weapon});
   if(_budgetToken>0 && player is {IsValid:true} && player.Slot==_budgetActor && player.PlayerPawn.Raw==_budgetPawn && e.Weapon.Replace("weapon_","")=="ak47")
   {
    if(++_budgetCount>=30)
    {
     long token=_budgetToken;int slot=_budgetActor;uint pawn=_budgetPawn;_budgetToken=0;
     Server.NextFrame(()=>{
      _api?.CancelUsercmdInjection(slot,token);
      var p=Utilities.GetPlayerFromSlot(slot);
      if(p is {IsValid:true} && p.PlayerPawn.Raw==pawn && _api!=null)_controls.Add((slot,_api.StartUsercmdSuppression(slot,(ulong)PlayerButtons.Attack),"suppress"));
     });
    }
   }
   return HookResult.Continue;
  });
  AddCommand("css_aim_probe","Controlled local capture fixture",(p,c)=>{
   if(p!=null)return;
   var bots=Utilities.GetPlayers().Where(b=>b.IsBot && b.TeamNum is 2 or 3 && b.PlayerPawn.Value is {IsValid:true}).ToArray();
   var action=c.ArgCount>1 ? c.GetArg(1) : "status";
   if(action=="prepare_open")
   {
    _api=new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
    foreach(var b in Utilities.GetPlayers().Where(b=>b.IsValid && b.IsBot && b.TeamNum is 2 or 3))
    {
     if(!b.PawnIsAlive)b.Respawn();
     AddTimer(.3f,()=>{
      if(!b.IsValid || !b.PawnIsAlive || _api==null)return;
      b.GiveNamedItem(b.TeamNum==2 ? "weapon_ak47" : "weapon_m4a1");
      if(!_api.Lock(b.Slot,LockTarget.Slot1))throw new InvalidOperationException("Preparation primary lock failed");
      _primaryLocks[b.Slot]=b.PlayerPawn.Raw;_api.SwitchBotWeapon(b.Slot,b.TeamNum==2 ? 7 : 16);
      b.PlayerPawn.Value!.Health=12000;
     });
    }
    Server.PrintToConsole("[PROBE] prepare guns at original spawn; wait for deploy and check live phase before contact");
   }
   if(action=="face_open")
   {
    if(Server.MapName!="de_dust2")throw new InvalidOperationException("Fixed view requires de_dust2");
    foreach(var b in bots.Where(b=>b.PawnIsAlive))
    {
     if(b.PlayerPawn.Value?.Bot is {} bot)
      _facePoses[bot.Handle]=(b.Slot,b.PlayerPawn.Raw,Server.CurrentTime+2,b.TeamNum==2 ? 29.85f : -150.15f);
    }
    Server.PrintToConsole("[PROBE] scripted pre-contact view, <=2s, released on native visual acquisition; no enemy or Attack injection");
   }
   if(action=="lock_primary")
   {
    _api=new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
    var actor=bots.First(b=>b.TeamNum==2 && b.PawnIsAlive);
    actor.GiveNamedItem("weapon_ak47");
    if(_api==null || !_api.Lock(actor.Slot,LockTarget.Slot1))throw new InvalidOperationException("Primary lock failed");
    _primaryLocks[actor.Slot]=actor.PlayerPawn.Raw;_api.SwitchBotWeapon(actor.Slot,7);
   }
   if(action=="contact_range")
   {
    if(Server.MapName!="de_dust2")throw new InvalidOperationException("Range fixture requires de_dust2");
    if(FixtureRules() is not {WarmupPeriod:false,FreezePeriod:false})
    {Server.PrintToConsole("[PROBE] refused range contact: warmup/freeze/unknown phase");return;}
    _api=new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
    var rangeActor=bots.First(b=>b.TeamNum==2);var rangeTarget=bots.First(b=>b.TeamNum==3);
    if(!rangeActor.PawnIsAlive)rangeActor.Respawn();
    if(!rangeTarget.PawnIsAlive)rangeTarget.Respawn();
    float distance=c.ArgCount>2 && float.TryParse(c.GetArg(2),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var requested)
     ? Math.Clamp(requested,300f,900f) : 600f;
    AddTimer(.4f,()=>{
     if(!rangeActor.IsValid || !rangeTarget.IsValid)return;
     if(rangeActor.PlayerPawn.Value is {} rap)rap.Health=12000;
     if(rangeTarget.PlayerPawn.Value is {} rtp)rtp.Health=12000;
     if(!TryPlaceRange(distance,rangeActor,rangeTarget))
      Server.PrintToConsole($"[PROBE] contact_range {distance:F0} unavailable: no clear ground sightline in searched directions");
    });
   }

   if(action=="dump_entities")
   {
    var path=Path.Combine(ModuleDirectory,"map-entities.jsonl");
    var lines=new List<string>();int dumped=0;
    foreach(var ent in Utilities.GetAllEntities())
    {
     if(ent==null || !ent.IsValid)continue;
     try
     {
      var baseEnt=ent.As<CBaseEntity>();
      var o=baseEnt.AbsOrigin;
      string cls=ent.DesignerName;
      float rp=0,ry=0;
      try{var r=baseEnt.AbsRotation;ry=r?.Y ?? 0f;rp=r?.X ?? 0f;}catch(Exception){}
      lines.Add(JsonSerializer.Serialize(new {classname=cls,x=o?.X,y=o?.Y,z=o?.Z,pitch=rp,yaw=ry}));
      dumped++;
     }
     catch(Exception){ }
    }
    File.WriteAllLines(path,lines);
    Server.PrintToConsole($"[PROBE] dumped {dumped} entities to {path}");
   }   if(action=="record_stop")StopMotionCapture();
   if(action=="record_start")
   {
    StopMotionCapture();_api=new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
    var actor=bots.First(b=>b.TeamNum==2 && b.PawnIsAlive);
    if(_api==null || !_api.StartRecord(actor.Slot))throw new InvalidOperationException("Native recording not ready");
    _recordSlot=actor.Slot;_recordPawn=actor.PlayerPawn.Raw;_recordUntil=Server.CurrentTime+10;
    _recordClock.Clear();Server.PrintToConsole("[PROBE] recording native motion for10 simulation seconds; no replay started");
   }
   if(action is "scenario" or "scenario_static" or "scenario_open" or "contact_open")
   {
    bool open=action is "scenario_open" or "contact_open";
    if(open && Server.MapName!="de_dust2")throw new InvalidOperationException("Open fixture coordinates require de_dust2");
    if(open && FixtureRules() is not {WarmupPeriod:false,FreezePeriod:false})
    {Server.PrintToConsole("[PROBE] refused open contact: warmup/freeze/unknown phase");return;}
    var a=bots.First(b=>b.TeamNum==2);var b=bots.First(b=>b.TeamNum==3);
    if(!a.PawnIsAlive)a.Respawn();if(!b.PawnIsAlive)b.Respawn();
    AddTimer(.5f,()=>{
     if(!a.IsValid || !b.IsValid)return;
     if(open && FixtureRules() is not {WarmupPeriod:false,FreezePeriod:false})return;
     if(action!="contact_open"){a.GiveNamedItem("weapon_ak47");b.GiveNamedItem("weapon_m4a1");}
     a.PlayerPawn.Value!.Health=12000;b.PlayerPawn.Value!.Health=12000;
     Utilities.SetStateChanged(a.PlayerPawn.Value!,"CBaseEntity","m_iHealth");Utilities.SetStateChanged(b.PlayerPawn.Value!,"CBaseEntity","m_iHealth");
     var pawn=a.PlayerPawn.Value!;var target=b.PlayerPawn.Value!;var pos=pawn.AbsOrigin!;
     // Keep observer's current view unchanged; place the target inside that field of view.
     float yaw=pawn.EyeAngles.Y*MathF.PI/180;
     if(open)
     {
      // Two actual spawn-ground positions from the user-provided Dust2 demo.
      // Static mesh says clear; an independent live brush trace is logged below.
      pawn.Teleport(new Vector(-1076,-843,116.789474f),new QAngle(0,29.85f,0),new Vector(0,0,0));
      target.Teleport(new Vector(-1015,-808,116.082634f),new QAngle(0,-150.15f,0),new Vector(0,0,0));
     }
     else target.Teleport(new Vector(pos.X+MathF.Cos(yaw)*100,pos.Y+MathF.Sin(yaw)*100,pos.Z),new QAngle(0,pawn.EyeAngles.Y+180,0),new Vector(0,0,0));
     File.AppendAllText(Path.Combine(ModuleDirectory,"fixture-events.jsonl"),JsonSerializer.Serialize(new {kind="scene_placed",scene=action,
      tick=Server.TickCount,time=Server.CurrentTime,observer_slot=a.Slot,observer_pawn=a.PlayerPawn.Raw,target_slot=b.Slot,target_pawn=b.PlayerPawn.Raw,
      warmup=FixtureRules()?.WarmupPeriod,freeze=FixtureRules()?.FreezePeriod})+"\n");
     AddTimer(.3f,()=>{
      if(!a.IsValid || !b.IsValid || !a.PawnIsAlive || !b.PawnIsAlive)return;
      var ap=a.PlayerPawn.Value!;var bp=b.PlayerPawn.Value!;var ao=ap.AbsOrigin!;var bo=bp.AbsOrigin!;
      var trace=Trace.TraceEndShape(new Vector(ao.X,ao.Y,ao.Z+ap.ViewOffset.Z),new Vector(bo.X,bo.Y,bo.Z+bp.ViewOffset.Z),options:new TraceOptions{InteractsWith=Masks.SolidBrushOnly});
      File.AppendAllText(Path.Combine(ModuleDirectory,"scene-traces.jsonl"),JsonSerializer.Serialize(new {time=Server.CurrentTime,scene=action,fraction=trace.Fraction,
       observer=new {x=ao.X,y=ao.Y,z=ao.Z,eye=ap.ViewOffset.Z,flags=ap.Flags},target=new {x=bo.X,y=bo.Y,z=bo.Z,eye=bp.ViewOffset.Z,flags=bp.Flags}})+"\n");
      Server.PrintToConsole($"[PROBE] live brush trace fraction={trace.Fraction}; not full visual truth");
     });
     if(action=="scenario")AddTimer(2f,()=>{
      if(!b.IsValid || !b.PawnIsAlive)return;
      var targetPawn=b.PlayerPawn.Value!;var current=targetPawn.AbsOrigin!;
      targetPawn.Teleport(new Vector(current.X-MathF.Sin(yaw)*25,current.Y+MathF.Cos(yaw)*25,current.Z),null,new Vector(0,0,0));
      Server.PrintToConsole("[PROBE] scripted lateral target step; not natural movement");
     });
     var api=new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
     if(action!="contact_open"){api?.SwitchBotWeapon(a.Slot,7);api?.SwitchBotWeapon(b.Slot,16);}
     Server.PrintToConsole("[PROBE] scripted positions/weapons; not a natural engagement");
    });
   }
   if(action=="force")
   {
    var a=bots.First(b=>b.TeamNum==2);var b=bots.First(b=>b.TeamNum==3);int remaining=200;
    uint aPawn=a.PlayerPawn.Raw,bPawn=b.PlayerPawn.Raw;
    _forcedPerception?.Kill();
    _forcedPerception=AddTimer(.05f,()=>{
     if(remaining--<=0 || !a.IsValid || !b.IsValid || a.PlayerPawn.Raw!=aPawn || b.PlayerPawn.Raw!=bPawn){_forcedPerception?.Kill();_forcedPerception=null;return;}
     foreach(var pair in new[]{(a,b),(b,a)})
     {
      if(!pair.Item1.PawnIsAlive || !pair.Item2.PawnIsAlive)continue;
      var bot=pair.Item1.PlayerPawn.Value?.Bot;if(bot==null)continue;
      bot.Enemy.Raw=pair.Item2.PlayerPawn.Raw;bot.IsEnemyVisible=true;bot.IsAttacking=true;
     }
    },CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT | CounterStrikeSharp.API.Modules.Timers.TimerFlags.STOP_ON_MAPCHANGE);
    Server.PrintToConsole("[PROBE] forced perception handles for adapter test only");
   }
   if(action is "stabilize_native_stance" or "stabilize_stand" or "stabilize_crouch" or "strafe_stand")
   {
    ReleaseControls();_api=new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
    if(_api==null)throw new InvalidOperationException("Fixture requires BotController");
    var actor=bots.First(b=>b.TeamNum==2);
    foreach(var b in bots.Where(b=>b.PawnIsAlive))
    {
     _controls.Add((b.Slot,_api.StartUsercmdMovement(b.Slot,0,b.Slot==actor.Slot && action=="strafe_stand" ? 1 : 0),"move"));
     ulong mask=(ulong)PlayerButtons.Jump;
     if(b.Slot!=actor.Slot || (action=="stabilize_stand" || action=="strafe_stand"))mask|=(ulong)PlayerButtons.Duck;
     if(b.Slot!=actor.Slot)mask|=(ulong)PlayerButtons.Attack;
     _controls.Add((b.Slot,_api.StartUsercmdSuppression(b.Slot,mask),"suppress"));
     if(b.Slot==actor.Slot && action=="stabilize_crouch")_controls.Add((b.Slot,_api.InjectUsercmd(b.Slot,(ulong)PlayerButtons.Duck,60000),"inject"));
    }
    if(_controls.Any(c=>c.Token<=0))throw new InvalidOperationException("Fixture control failed");
    Server.PrintToConsole("[PROBE] input condition="+action+"; scripted movement suppression, not gameplay policy");
   }
   if(action=="refill")
   {
    foreach(var b in bots.Where(b=>b.PawnIsAlive))
    {
     var pawn=b.PlayerPawn.Value!;var gun=pawn.WeaponServices?.ActiveWeapon.Value?.As<CCSWeaponBase>();
     if(gun!=null){gun.Clip1=30;pawn.ShotsFired=0;Utilities.SetStateChanged(gun,"CBasePlayerWeapon","m_iClip1");}
     pawn.Health=12000;
    }
    Server.PrintToConsole("[PROBE] scripted refill; never treat as natural reload evidence");
   }
   if(action is "drive_look" or "drive_spot" or "drive_goal")
   {
    int remaining=320;
    AddTimer(.015625f,()=>{
     if(remaining--<=0)return;
     foreach(var b in bots.Where(b=>b.IsValid && b.PawnIsAlive))
     {
      var pawn=b.PlayerPawn.Value!;var bot=pawn.Bot;if(bot==null)continue;
      var eye=pawn.AbsOrigin!;var point=bot.TargetSpot;
      float dx=point.X-eye.X,dy=point.Y-eye.Y,dz=point.Z-eye.Z-pawn.ViewOffset.Z;
      float yaw=MathF.Atan2(dy,dx)*180/MathF.PI,pitch=-MathF.Atan2(dz,MathF.Sqrt(dx*dx+dy*dy))*180/MathF.PI;
      if(action=="drive_look"){bot.LookYaw=yaw;bot.LookPitch=pitch;}
      if(action=="drive_goal"){bot.AimGoal.Y=yaw;bot.AimGoal.X=pitch;}
      if(action=="drive_spot"){var look=bot.LookAtSpot;look.X=point.X;look.Y=point.Y;look.Z=point.Z;bot.LookAtSpotAttack=true;}
     }
    },CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
    Server.PrintToConsole("[PROBE] native control-surface diagnostic "+action);
   }
   if(action is "shoot" or "shoot_one")
   {
    var api=new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
    _api=api;
    foreach(var b in bots.Where(b=>b.PawnIsAlive).Take(action=="shoot_one" ? 1 : 2))
    {
     long token=api?.InjectUsercmd(b.Slot,(ulong)PlayerButtons.Attack,action=="shoot_one" ? 60000 : 1200) ?? -1;
     if(action=="shoot_one"){_budgetActor=b.Slot;_budgetPawn=b.PlayerPawn.Raw;_budgetCount=0;_budgetToken=token;}
     _controls.Add((b.Slot,token,"inject"));
     Server.PrintToConsole($"[PROBE] injected attack slot={b.Slot} token={token}; shoot_one ends at30 AK events or60s wall-clock lease");
    }
   }
   _api??=new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
   Server.PrintToConsole($"[PROBE] phase time={Server.CurrentTime} warmup={FixtureRules()?.WarmupPeriod} freeze={FixtureRules()?.FreezePeriod}");
   foreach(var b in bots.Take(4))Server.PrintToConsole($"[PROBE] slot={b.Slot} alive={b.PawnIsAlive} weapon={b.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value?.DesignerName} controller_flags={b.Flags:X} all_lock={_api?.IsLocked(b.Slot,LockKind.All)} aim_lock={_api?.IsLocked(b.Slot,LockKind.Aim)} weapon_lock={_api?.GetWeaponLock(b.Slot)} replay={_api?.IsReplaying(b.Slot)} enemy={b.PlayerPawn.Value?.Bot?.Enemy.Raw}");
  });
 }
}
