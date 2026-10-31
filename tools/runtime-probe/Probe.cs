using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;
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
 private readonly HashSet<int> _primaryLocks=new();
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
  foreach(int slot in _primaryLocks)_api?.Unlock(slot,LockKind.Weapon);
  _primaryLocks.Clear();
  foreach(var c in _controls)try { if(c.Kind=="move")_api?.CancelUsercmdMovement(c.Slot,c.Token);else if(c.Kind=="suppress")_api?.CancelUsercmdSuppression(c.Slot,c.Token);else _api?.CancelUsercmdInjection(c.Slot,c.Token); }catch { }
  _controls.Clear();
 }
 public override void Unload(bool hotReload)=>ReleaseControls();
 public override string ModuleName=>"HumanAimProbe";
 public override string ModuleVersion=>"2-capture-fixture";
 public override void Load(bool hotReload)
 {
  // Never run this fixture in a normal client. Deliberately constrained to our test server.
  var args=Environment.GetCommandLineArgs();
  int port=Array.IndexOf(args,"-port"),ip=Array.IndexOf(args,"-ip");
  if(!args.Contains("-dedicated") || port<0 || port+1>=args.Length || args[port+1]!="27025"
     || ip<0 || ip+1>=args.Length || args[ip+1]!="127.0.0.1")return;
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
   if(action=="lock_primary")
   {
    _api=new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
    var actor=bots.First(b=>b.TeamNum==2 && b.PawnIsAlive);
    actor.GiveNamedItem("weapon_ak47");
    if(_api==null || !_api.Lock(actor.Slot,LockTarget.Slot1))throw new InvalidOperationException("Primary lock failed");
    _primaryLocks.Add(actor.Slot);_api.SwitchBotWeapon(actor.Slot,7);
   }
   if(action=="record_stop")StopMotionCapture();
   if(action=="record_start")
   {
    StopMotionCapture();_api=new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
    var actor=bots.First(b=>b.TeamNum==2 && b.PawnIsAlive);
    if(_api==null || !_api.StartRecord(actor.Slot))throw new InvalidOperationException("Native recording not ready");
    _recordSlot=actor.Slot;_recordPawn=actor.PlayerPawn.Raw;_recordUntil=Server.CurrentTime+10;
    _recordClock.Clear();Server.PrintToConsole("[PROBE] recording native motion for10 simulation seconds; no replay started");
   }
   if(action is "scenario" or "scenario_static")
   {
    var a=bots.First(b=>b.TeamNum==2);var b=bots.First(b=>b.TeamNum==3);
    if(!a.PawnIsAlive)a.Respawn();if(!b.PawnIsAlive)b.Respawn();
    AddTimer(.5f,()=>{
     if(!a.IsValid || !b.IsValid)return;
     a.GiveNamedItem("weapon_ak47");b.GiveNamedItem("weapon_m4a1");
     a.PlayerPawn.Value!.Health=12000;b.PlayerPawn.Value!.Health=12000;
     Utilities.SetStateChanged(a.PlayerPawn.Value!,"CBaseEntity","m_iHealth");Utilities.SetStateChanged(b.PlayerPawn.Value!,"CBaseEntity","m_iHealth");
     var pawn=a.PlayerPawn.Value!;var target=b.PlayerPawn.Value!;var pos=pawn.AbsOrigin!;
     // Keep observer's current view unchanged; place the target inside that field of view.
     float yaw=pawn.EyeAngles.Y*MathF.PI/180;
     target.Teleport(new Vector(pos.X+MathF.Cos(yaw)*100,pos.Y+MathF.Sin(yaw)*100,pos.Z),new QAngle(0,pawn.EyeAngles.Y+180,0),new Vector(0,0,0));
     if(action=="scenario")AddTimer(2f,()=>{
      if(!b.IsValid || !b.PawnIsAlive)return;
      var targetPawn=b.PlayerPawn.Value!;var current=targetPawn.AbsOrigin!;
      targetPawn.Teleport(new Vector(current.X-MathF.Sin(yaw)*25,current.Y+MathF.Cos(yaw)*25,current.Z),null,new Vector(0,0,0));
      Server.PrintToConsole("[PROBE] scripted lateral target step; not natural movement");
     });
     var api=new PluginCapability<IBotControllerApi>("botcontroller:api").Get();
     api?.SwitchBotWeapon(a.Slot,7);api?.SwitchBotWeapon(b.Slot,16);
     Server.PrintToConsole("[PROBE] scripted positions/weapons; not a natural engagement");
    });
   }
   if(action=="force")
   {
    var a=bots.First(b=>b.TeamNum==2);var b=bots.First(b=>b.TeamNum==3);int remaining=200;
    AddTimer(.05f,()=>{
     if(remaining--<=0 || !a.IsValid || !b.IsValid)return;
     foreach(var pair in new[]{(a,b),(b,a)})
     {
      if(!pair.Item1.PawnIsAlive || !pair.Item2.PawnIsAlive)continue;
      var bot=pair.Item1.PlayerPawn.Value?.Bot;if(bot==null)continue;
      bot.Enemy.Raw=pair.Item2.PlayerPawn.Raw;bot.IsEnemyVisible=true;bot.IsAttacking=true;
     }
    },CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
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
   foreach(var b in bots.Take(4))Server.PrintToConsole($"[PROBE] slot={b.Slot} alive={b.PawnIsAlive} weapon={b.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value?.DesignerName}");
  });
 }
}
