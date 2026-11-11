using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vec=System.Numerics.Vector3;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private readonly Dictionary<int,(uint Pawn,uint Weapon,CombatHistory History)> _combatHistories=new();
    private sealed class TargetNotices
    {
        public uint Pawn;
        public float ScanAt;
        public VisibleTargetHistory Seen=new();
    }
    private readonly Dictionary<int,TargetNotices> _noticedTargets=new();
    private CombatHistory HistoryFor(CCSPlayerController p,CCSPlayerPawn pawn)
    {
        uint weapon=pawn.WeaponServices?.ActiveWeapon.Raw??uint.MaxValue;
        if(!_combatHistories.TryGetValue(p.Slot,out var entry)||entry.Pawn!=p.PlayerPawn.Raw||entry.Weapon!=weapon)
            _combatHistories[p.Slot]=entry=(p.PlayerPawn.Raw,weapon,new());
        return entry.History;
    }
    private void CleanCombatHistories()
    {
        // .NET 10 dictionary enumeration permits Remove; this loop never adds.
        foreach(var (slot,entry) in _combatHistories)
        {
            var p=EntityPlayerAtSlot(slot);
            if(p is not {IsValid:true,IsBot:true,PawnIsAlive:true}||p.HasBeenControlledByPlayerThisRound||p.PlayerPawn.Raw!=entry.Pawn)
            {_combatHistories.Remove(slot);_noticedTargets.Remove(slot);}
        }
    }
    private HumanState CreateHumanState(CCSPlayerController p,CCSPlayerPawn pawn,CCSPlayerPawn enemy,nint pointer,HumanAimProfile profile,float now)
    {
        var history=HistoryFor(p,pawn);
        float credit=_noticedTargets.TryGetValue(p.Slot,out var notices)&&notices.Pawn==p.PlayerPawn.Raw
            ?notices.Seen.ReactionCredit(enemy.EntityHandle.Raw,now,profile):0;
        var state=new HumanState{Pointer=pointer,Pawn=p.PlayerPawn.Raw,Enemy=enemy.EntityHandle.Raw,
            PlannedWeapon=pawn.WeaponServices?.ActiveWeapon.Raw??uint.MaxValue,Combat=history,
            Model=new(profile,new(pawn.EyeAngles.X,pawn.EyeAngles.Y),now,Random.Shared.Next(),credit)};
        if(history.LastTarget==enemy.EntityHandle.Raw&&now>=history.LastShotAt&&now-history.LastShotAt<=.15f)
        {
            state.Model.CommitShot(now,new(pawn.EyeAngles.X,pawn.EyeAngles.Y));
            state.AllowLatchUntil=history.LastShotAt+.15f;
        }
        WriteTelemetry(new{kind="combat_plan_created",time=now,slot=p.Slot,enemy=enemy.EntityHandle.Raw,
            reaction_credit_ms=credit*1000,carried_shots=history.SprayShots,same_target_shot=history.LastTarget==enemy.EntityHandle.Raw});
        return state;
    }
    private void NoticeAlternativeTargets(CCSPlayerController p,CCSPlayerPawn pawn,Vector eye,float now)
    {
        if(!_noticedTargets.TryGetValue(p.Slot,out var notices)||notices.Pawn!=p.PlayerPawn.Raw)
            _noticedTargets[p.Slot]=notices=new(){Pawn=p.PlayerPawn.Raw};
        if(now<notices.ScanAt||pawn.BlindUntilTime>now)return;
        notices.ScanAt=now+.10f;
        var from=new Vec(eye.X,eye.Y,eye.Z);
        var view=new System.Numerics.Vector2(pawn.EyeAngles.X,pawn.EyeAngles.Y);
        foreach(var candidate in EntityPlayers())
        {
            if(candidate is not {IsValid:true,PawnIsAlive:true}||candidate.TeamNum==p.TeamNum||candidate.TeamNum is not (2 or 3)
                ||candidate.PlayerPawn.Value is not {IsValid:true} target||!TryReadTargetGeometry(target,out var geometry))continue;
            foreach(int part in _attentionParts)
            {
                if(!TryComputePartPos(geometry,part,out var x,out var y,out var z))continue;
                var point=new Vec(x,y,z);var delta=AimMath.Delta(AimMath.Angles(from,point),view);
                if(MathF.Abs(delta.Y)>_humanSettings.HorizontalFieldOfView*.5f||MathF.Abs(delta.X)>50
                    ||HumanSmokeBlocks(eye,point)||!PointVisibleFromEye(eye,x,y,z,.999f,out _))continue;
                notices.Seen.Notice(target.EntityHandle.Raw,now);break;
            }
        }
    }
}
