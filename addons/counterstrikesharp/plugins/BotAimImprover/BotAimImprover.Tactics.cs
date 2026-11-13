using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System.Numerics;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private readonly Dictionary<int,float> _plantingUntil=new();
    private readonly Dictionary<int,string> _tacticalReasons=new();
    private float _objectiveScanAt,_roundDeadline,_bombDeadline,_roundStartedAt;
    private bool _roundLive,_bombPlanted;
    private Vector3 _bombPosition;
    private void InitializeTactics()
    {
        RegisterEventHandler<EventBombBeginplant>((e,i)=>{
            if(e.Userid is {IsValid:true} p){_plantingUntil[p.Slot]=Server.CurrentTime+5;ResetSilentMovement(p.Slot);CancelCoverPeek(p.Slot,"planting");}
            return HookResult.Continue;
        });
        RegisterEventHandler<EventBombAbortplant>((e,i)=>{if(e.Userid is {} p)_plantingUntil.Remove(p.Slot);return HookResult.Continue;});
        RegisterEventHandler<EventBombPlanted>((e,i)=>{_plantingUntil.Clear();_objectiveScanAt=0;return HookResult.Continue;});
        RegisterEventHandler<EventBombBegindefuse>((e,i)=>{
            if(e.Userid is {} p){ResetSilentMovement(p.Slot);CancelCoverPeek(p.Slot,"defusing");}
            return HookResult.Continue;
        });
    }
    private void ResetTactics()
    {
        _plantingUntil.Clear();_tacticalReasons.Clear();_objectiveScanAt=0;_roundLive=false;
        _bombPlanted=false;_roundDeadline=_bombDeadline=0;
        ResetCoverPeeks();ResetTeamNavigation();
    }
    private void ReadObjective(float now)
    {
        if(now<_objectiveScanAt)return;
        _objectiveScanAt=now+.2f;
        var rules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault(p=>p.IsValid)?.GameRules;
        _roundLive=rules!=null&&!rules.FreezePeriod&&!rules.WarmupPeriod;
        _roundDeadline=rules==null?now:rules.RoundStartTime+rules.RoundTime;
        _roundStartedAt=rules?.RoundStartTime??now;
        _bombPlanted=rules?.BombPlanted==true;_bombDeadline=now;_bombPosition=Vector3.Zero;
        if(!_bombPlanted)return;
        var bomb=Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4")
            .FirstOrDefault(b=>b.IsValid&&b.BombTicking&&!b.BombDefused);
        if(bomb?.AbsOrigin is not {} o)return;
        _bombDeadline=bomb.C4Blow;_bombPosition=new(o.X,o.Y,o.Z);
    }
    private TacticalMovementDecision MovementContext(CCSPlayerController p,CCSPlayerPawn pawn,CCSBot bot,float now)
    {
        ReadObjective(now);
        if((pawn.Flags&1)==0||pawn.MoveType==MoveType_t.MOVETYPE_LADDER)
            return new(false,false,"airborne-or-ladder");
        bool busy=pawn.IsDefusing||(_plantingUntil.TryGetValue(p.Slot,out var until)&&now<until);
        bool combat=bot.IsEnemyVisible||bot.IsAttacking
            ||(_humanStates.TryGetValue(p.Slot,out var s)&&now<s.UnderFireUntil);
        bool carrying=pawn.WeaponServices?.MyWeapons.Any(h=>h.Value is {IsValid:true} w&&w.DesignerName=="weapon_c4")==true;
        var o=pawn.AbsOrigin;
        float distance=o==null?0:Vector3.Distance(new(o.X,o.Y,o.Z),_bombPosition);
        var decision=TacticalMovementPolicy.Evaluate(new(combat,busy,!_roundLive,p.TeamNum==3,
            carrying,MathF.Max(0,_roundDeadline-now),_bombPlanted,MathF.Max(0,_bombDeadline-now),distance,p.PawnHasDefuser?5:10));
        _tacticalReasons[p.Slot]=decision.Reason;
        return decision;
    }
    private void MaintainTacticalMovement()
    {
        if(RetakeOwnsGoals)return;
        foreach(var slot in _silentStates.Keys.Concat(_coverPeeks.Keys).Distinct().ToArray())
        {
            var p=EntityPlayerAtSlot(slot);
            if(!HumanActive||p is not {IsValid:true,IsBot:true,PawnIsAlive:true}||p.HasBeenControlledByPlayerThisRound
                ||p.PlayerPawn.Value is not {IsValid:true} pawn||pawn.Bot is not {} bot||OtherViewOwner(slot))
            {ResetSilentMovement(slot);CancelCoverPeek(slot,"ownership-ended");continue;}
            float now=Server.CurrentTime;
            var decision=MovementContext(p,pawn,bot,now);
            if(!decision.Quiet)ResetSilentMovement(slot);
            if(!decision.Peek)CancelCoverPeek(slot,decision.Reason);
            else MaintainCoverPeek(p,pawn,bot,now);
        }
    }
}
