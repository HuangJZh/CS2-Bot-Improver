using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using InventorySimulator;
using System.Reflection;
using System.Text.Json;
public class InventoryProbe:BasePlugin {
 public override string ModuleName=>"InventoryProbe";
 public override string ModuleVersion=>"1.0";
 bool scheduled; int stage; float waitUntil; float deadline;
 void Record(string phase) {try {
  var local=typeof(InventorySimulator.InventorySimulator).Assembly.GetType("InventorySimulator.LocalOnly")!;
  var players=Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").Where(x=>x.IsValid&&!x.IsBot).ToArray();
  var result=players.Select(p=>{var state=p.GetState(); var inv=state.Inventory;
   return new {slot=p.Slot,ip=p.IpAddress,team=p.TeamNum,canApply=local.GetMethod("CanApply",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[p]),
    active=local.GetMethod("IsActiveSession",BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,null),
    cached=inv!=null,fetching=state.IsFetching,expected=inv?.GetAllWeapons().Select(w=>new {w.Def,w.Paint,w.Uid}).ToArray(),
    weapons=p.PlayerPawn.Value?.WeaponServices?.MyWeapons.Select(h=>h.Value).Where(w=>w!=null&&w.IsValid).Select(w=>new {name=w!.DesignerName,def=w.AttributeManager.Item.ItemDefinitionIndex,itemId=w.AttributeManager.Item.ItemID,account=w.AttributeManager.Item.AccountID,clip=w.Clip1,reserve=w.ReserveAmmo[0],attributes=w.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.Count}).ToArray()};}).ToArray();
  File.AppendAllText(Path.Combine(ModuleDirectory,"evidence.jsonl"),JsonSerializer.Serialize(new {phase,map=Server.MapName,players=result})+"\n");
 }catch(Exception e){File.AppendAllText(Path.Combine(ModuleDirectory,"errors.txt"),e.ToString()+"\n");}}
 public override void Load(bool hotReload) {System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(GetType().Assembly)!.Resolving += (_, n) => AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a=>a.GetName().Name==n.Name);var a=Environment.GetCommandLineArgs();int i=Array.IndexOf(a,"-ip"),p=Array.IndexOf(a,"-port");if(i<0||p<0||a[i+1]!="127.0.0.1"||a[p+1]!="27025")return;
 RegisterListener<Listeners.OnMapStart>(_=>Schedule());if(Server.MapName=="de_mirage")Schedule();}
 CCSPlayerController? Host()=>Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").FirstOrDefault(x=>x.IsValid&&!x.IsBot);
 void Schedule(){
  if(Server.MapName=="de_dust2") {stage=5;waitUntil=Server.CurrentTime+18;deadline=Server.CurrentTime+100;return;}
  if(scheduled)return;scheduled=true;stage=0;waitUntil=Server.CurrentTime+18;deadline=Server.CurrentTime+120;
  AddTimer(1,Advance,TimerFlags.REPEAT);
 }
 void Advance(){try {
  if(Server.CurrentTime<waitUntil)return;
  var host=Host();if(host==null)return;
  var state=host.GetState();
  if(Server.CurrentTime>deadline){Record("timeout-stage-"+stage);Server.ExecuteCommand("quit");return;}
  if(stage==0){
   if(state.IsFetching || state.Inventory==null)return;
   Record("initial");host.SwitchTeam(CsTeam.CounterTerrorist);host.Respawn();
   Server.ExecuteCommand("mp_warmup_end;mp_freezetime 3;mp_startmoney 16000;mp_buy_anywhere 1;mp_buytime 999;mp_restartgame 1;bot_aim_capture start 60");
   stage=1;waitUntil=Server.CurrentTime+8;return;
  }
  if(stage==1){
   var pawn=host.PlayerPawn.Value;if(pawn==null||!host.PawnIsAlive){host.Respawn();return;}
   pawn.Health=10000;
   foreach(var h in pawn.WeaponServices!.MyWeapons.ToArray()){var w=h.Value;if(w?.IsValid==true && w.DesignerName=="weapon_m4a1"){pawn.WeaponServices.As<CCSPlayer_WeaponServices>().DropWeapon(w);w.Remove();}}
   state.Inventory=null;state.WsUpdatedAt=0;
   var gun=host.GiveNamedItem<CCSWeaponBase>("weapon_m4a1_silencer");
   if(gun!=null){gun.OriginalOwnerXuidLow=(uint)host.SteamID;gun.OriginalOwnerXuidHigh=(uint)(host.SteamID>>32);gun.AttributeManager.Item.ItemID=1;gun.AttributeManager.Item.NetworkedDynamicAttributes.Attributes.RemoveAll();gun.Clip1=7;gun.ReserveAmmo[0]=19;}
   Record("before-delayed-initial-download");host.RefreshInventory();stage=2;deadline=Server.CurrentTime+105;waitUntil=Server.CurrentTime+2;return;
  }
  if(stage==2){
   if(state.IsFetching||state.Inventory==null)return;
   Record("after-delayed-initial-download");stage=3;waitUntil=Server.CurrentTime+3;return;
  }
  if(stage==3){
   Record("before-ws");state.WsUpdatedAt=0;Server.ExecuteCommand("css_ws");stage=4;waitUntil=Server.CurrentTime+2;deadline=Server.CurrentTime+105;return;
  }
  if(stage==4){
   if(state.IsFetching||state.WsUpdatedAt==0)return;
   Record("after-ws");stage=5;Server.ExecuteCommand("bot_aim_capture stop;map de_dust2");return;
  }
  if(stage==5 && Server.MapName=="de_dust2"){
   if(state.IsFetching||state.Inventory==null)return;
   Record("after-map-change");Server.ExecuteCommand("mp_warmup_end;bot_aim_capture start 25");stage=6;waitUntil=Server.CurrentTime+28;deadline=Server.CurrentTime+50;return;
  }
  if(stage==6){Record("map-change-finish");Server.ExecuteCommand("bot_aim_capture stop;quit");stage=7;}
 }catch(Exception e){File.AppendAllText(Path.Combine(ModuleDirectory,"errors.txt"),e.ToString()+"\n");Server.ExecuteCommand("quit");}}
}