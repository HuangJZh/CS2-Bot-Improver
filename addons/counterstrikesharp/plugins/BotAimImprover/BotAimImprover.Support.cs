using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Vec=System.Numerics.Vector3;
using Angles=System.Numerics.Vector2;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private sealed class SupportPlan
    {
        public required uint Pawn;
        public required int Team;
        public required List<int> Path;
        public required Vec Anchor;
        public Vec? ThreatSnapshot;
        public float Started,LastProgress,LogAt,Best=float.PositiveInfinity;
        public int Cursor;
    }
    private readonly Dictionary<int,SupportPlan> _supportPlans=new();
    private readonly Dictionary<int,float> _supportCooldown=new();
    private float _supportTick;
    private int _supportAssigned,_supportEngaged;
    private void InitializeTeamSupport()
    {
        RegisterEventHandler<EventPlayerHurt>((e,i)=>{
            if(e.Userid is {IsValid:true} mate&&e.Attacker is {IsValid:true} attacker&&attacker.TeamNum!=mate.TeamNum
                &&HumanFireContext.Classify(e.Weapon?.StartsWith("weapon_")==true?e.Weapon:"weapon_"+e.Weapon)!=AimWeaponClass.Other)
                RequestTeamSupport(mate);
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerDeath>((e,i)=>{
            if(e.Userid is {IsValid:true} mate&&e.Attacker is {IsValid:true} attacker&&attacker.TeamNum!=mate.TeamNum)
                RequestTeamSupport(mate);
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundEnd>((e,i)=>{ResetTeamSupport();return HookResult.Continue;});
        AddCommand("bot_team_support","Local host: on/off/status",(caller,c)=>{
            if(!IsLocalAimConsole(caller))return;
            var mode=c.ArgCount>1?c.GetArg(1):"status";
            if(mode is "on" or "off"){_humanSettings.TeamSupport=mode=="on";ResetTeamSupport();}
            Server.PrintToConsole($"[TeamSupport] enabled={_humanSettings.TeamSupport} active={_supportPlans.Count} assigned={_supportAssigned} engaged={_supportEngaged}");
        });
    }
    private void ResetTeamSupport(){_supportPlans.Clear();_supportCooldown.Clear();_supportTick=0;}
    private bool SupportAvailable(CCSPlayerController p,CCSPlayerPawn pawn,CCSBot bot,float now)
    {
        if(!HumanActive||!_humanSettings.TeamSupport||!_humanSettings.TeamNavigation||_navigationMoveTo==null||_navigationFailed
            ||p.HasBeenControlledByPlayerThisRound||!IsGun(pawn)||pawn.IsDefusing||OtherViewOwner(p.Slot)
            ||bot.IsEnemyVisible||bot.IsAttacking
            ||(pawn.Flags&1)==0||pawn.MoveType==MoveType_t.MOVETYPE_LADDER
            ||(_plantingUntil.TryGetValue(p.Slot,out var until)&&now<until)
            ||(_humanStates.TryGetValue(p.Slot,out var state)&&now<state.UnderFireUntil))return false;
        if(pawn.WeaponServices?.MyWeapons.Any(w=>w.Value?.DesignerName=="weapon_c4")==true)return false;
        ReadObjective(now);
        return _roundLive&&!_navigationRoundEnded&&_roundDeadline-now>15
            &&(!_bombPlanted||_bombDeadline-now>18);
    }
    private void RequestTeamSupport(CCSPlayerController source)
    {
        if(RetakeOwnsGoals)return;
        float now=Server.CurrentTime;
        if(!HumanActive||!_humanSettings.TeamSupport||!_humanSettings.TeamNavigation||source.TeamNum is not (2 or 3)
            ||source.PlayerPawn.Value is not {IsValid:true} sp||sp.AbsOrigin==null
            ||now<_supportCooldown.GetValueOrDefault(source.TeamNum)||_supportPlans.Values.Any(p=>p.Team==source.TeamNum))return;
        ReadObjective(now);
        if(!_roundLive||_navigationRoundEnded||!LoadTeamNavigation())return;
        var anchor=Position(sp);
        if(source.PawnIsAlive)
        {
            float yaw=sp.EyeAngles.Y*MathF.PI/180;
            anchor-=new Vec(MathF.Cos(yaw),MathF.Sin(yaw),0)*72;
        }
        if(_navigationGraph!.Locate(anchor,180) is not {} goal)return;
        anchor=_navigationGraph.Areas[goal].Point;
        Vec? snapshot=null;
        if(_humanStates.TryGetValue(source.Slot,out var sight)&&sight.Pawn==source.PlayerPawn.Raw&&sight.LastSight
            &&now-sight.Model.LastVisible<.2f&&sight.LastSampleResult.StartsWith("ok:")&&sight.LastVisualEstimate is {} estimate)
            snapshot=estimate.Position;
        CCSPlayerController? chosen=null;List<int>? chosenPath=null;float best=float.PositiveInfinity;
        foreach(var p in EntityPlayers())
        {
            if(p is not {IsValid:true,IsBot:true,PawnIsAlive:true}||p.Slot==source.Slot||p.TeamNum!=source.TeamNum
                ||p.PlayerPawn.Value is not {IsValid:true} pawn||pawn.AbsOrigin==null||pawn.Bot is not {} bot)continue;
            var position=Position(pawn);
            if(!TeamSupportPolicy.Candidate(position,anchor,SupportAvailable(p,pawn,bot,now))
                ||_navigationGraph.Locate(position) is not {} start)continue;
            var path=_navigationGraph.Path(start,goal);if(path==null||path.Count<2)continue;
            float length=_navigationGraph.Length(path);
            if(!TeamSupportPolicy.Route(length,Vec.Distance(position,anchor))||length>=best)continue;
            chosen=p;chosenPath=path;best=length;
        }
        if(chosen==null||chosenPath==null)return;
        EndNavigation(chosen.Slot,"team-support");ResetSilentMovement(chosen.Slot);CancelCoverPeek(chosen.Slot,"team-support");
        _supportPlans[chosen.Slot]=new(){Pawn=chosen.PlayerPawn.Raw,Team=chosen.TeamNum,Path=chosenPath,Anchor=anchor,
            ThreatSnapshot=snapshot,Started=now,LastProgress=now};
        _supportCooldown[source.TeamNum]=now+3;_supportAssigned++;
        WriteTelemetry(new{kind="team_support_assigned",time=now,slot=chosen.Slot,source_slot=source.Slot,
            source_pawn=source.PlayerPawn.Raw,team=chosen.TeamNum,path_length=best,has_visual_snapshot=snapshot.HasValue,
            anchor_x=anchor.X,anchor_y=anchor.Y,anchor_z=anchor.Z});
    }
    private void TickTeamSupport()
    {
        float now=Server.CurrentTime;if(now<_supportTick)return;_supportTick=now+.1f;
        foreach(var (slot,plan) in _supportPlans.ToArray())
        {
            var p=EntityPlayerAtSlot(slot);
            bool valid=p is {IsValid:true,IsBot:true,PawnIsAlive:true}&&p.PlayerPawn.Raw==plan.Pawn&&p.TeamNum==plan.Team;
            var pawn=valid?p!.PlayerPawn.Value:null;var bot=pawn?.Bot;
            bool available=pawn is {IsValid:true}&&bot!=null&&pawn.AbsOrigin!=null&&_navigationGraph!=null&&SupportAvailable(p!,pawn,bot,now);
            float distance=available?Vec.Distance(Position(pawn!),plan.Anchor):0;
            if(distance<plan.Best-12){plan.Best=distance;plan.LastProgress=now;}
            if(!TeamSupportPolicy.Continue(now,plan.Started,plan.LastProgress,distance,available))
            {
                if(bot?.IsEnemyVisible==true)_supportEngaged++;
                _supportPlans.Remove(slot);
                WriteTelemetry(new{kind="team_support_end",time=now,slot,enemy_visible=bot?.IsEnemyVisible,distance});continue;
            }
            if(now-plan.Started<.15f)continue; // finite communication/decision delay
            var origin=Position(pawn!);var nodes=plan.Path;
            while(plan.Cursor<nodes.Count-1&&Vec.Distance(origin,_navigationGraph!.Areas[nodes[plan.Cursor]].Point)<125)plan.Cursor++;
            var destination=_navigationGraph!.Areas[nodes[plan.Cursor]].Point;
            _navigationTarget.X=destination.X;_navigationTarget.Y=destination.Y;_navigationTarget.Z=destination.Z;
            _navigationMoveTo!.Invoke(bot!.Handle,_navigationTarget.Handle,1);
            if(now>=plan.LogAt)
            {
                plan.LogAt=now+.2f;
                WriteTelemetry(new{kind="team_support_step",time=now,slot,distance,cursor=plan.Cursor,
                    x=origin.X,y=origin.Y,z=origin.Z});
            }
        }
    }
    private bool TryApplySupportPreaim(CCSPlayerController p,CCSBot bot,nint pointer,CCSPlayerPawn pawn,Vec eye,float now)
    {
        if(!_supportPlans.TryGetValue(p.Slot,out var plan)||plan.Pawn!=p.PlayerPawn.Raw||now-plan.Started is <.15f or >1f
            ||plan.ThreatSnapshot is not {} snapshot||!SupportAvailable(p,pawn,bot,now))return false;
        var profile=CombatProfile(p,now);
        var current=new Angles(pawn.EyeAngles.X,pawn.EyeAngles.Y);
        var next=AimMath.Advance(current,AimMath.Angles(eye,snapshot),AttentionDelta(p.Slot,now),profile.TrackingTime,profile.MaxSpeed);
        var spot=AimMath.Ray(eye,next);_modelSpots[pointer]=spot;WriteModelSpot(bot,spot);RefreshFireGuard(p);
        return true;
    }
}
