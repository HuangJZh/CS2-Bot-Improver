using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using Vec=System.Numerics.Vector3;
using Angles=System.Numerics.Vector2;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private sealed record CoverPeek(uint Pawn,PeekRoute Route,long Token,float Started,float Until,float ViewYaw);
    private readonly Dictionary<int,CoverPeek> _coverPeeks=new();
    private readonly Dictionary<int,float> _peekNext=new();
    private static Vec Position(CCSPlayerPawn pawn){var p=pawn.AbsOrigin!;return new(p.X,p.Y,p.Z);}
    private static Vec NavGoal(CCSBot bot){var p=bot.GoalPosition;return new(p.X,p.Y,p.Z);}
    private static bool ClearSegment(Vec a,Vec b)
    {
        var trace=Trace.TraceEndShape(new Vector(a.X,a.Y,a.Z),new Vector(b.X,b.Y,b.Z),options:new TraceOptions{InteractsWith=Masks.SolidBrushOnly});
        return trace.Fraction>=.999f;
    }
    private static bool SafeGround(Vec start,Vec destination)
    {
        foreach(var d in new[]{new Vec(0,0,0),new Vec(12,12,0),new Vec(12,-12,0),new Vec(-12,12,0),new Vec(-12,-12,0)})
        {
            var p=destination+d;var a=new Vector(p.X,p.Y,start.Z+18);var b=new Vector(p.X,p.Y,start.Z-40);
            var trace=Trace.TraceEndShape(a,b,options:new TraceOptions{InteractsWith=Masks.SolidBrushOnly});
            float floor=a.Z+(b.Z-a.Z)*trace.Fraction;
            if(trace.Fraction>=.999f||MathF.Abs(floor-start.Z)>10)return false;
        }
        return true;
    }
    private static bool BodyPathClear(Vec start,Vec end,float eyeHeight)
    {
        foreach(float z in new[]{12f,36f,eyeHeight})
            foreach(var d in new[]{new Vec(0,0,z),new Vec(16,16,z),new Vec(-16,16,z),new Vec(16,-16,z),new Vec(-16,-16,z)})
                if(!ClearSegment(start+d,end+d))return false;
        return SafeGround(start,(start+end)*.5f)&&SafeGround(start,end);
    }
    private void CancelCoverPeek(int slot,string reason)
    {
        if(!_coverPeeks.Remove(slot,out var peek))return;
        if(peek.Token>0)try{_humanController?.CancelUsercmdInjection(slot,peek.Token);}catch(Exception){}
        WriteTelemetry(new{kind="cover_peek_end",tick=Server.TickCount,time=Server.CurrentTime,slot,reason});
    }
    private void ResetCoverPeeks(){foreach(int slot in _coverPeeks.Keys.ToArray())CancelCoverPeek(slot,"reset");_peekNext.Clear();}
    private void MaintainCoverPeek(CCSPlayerController p,CCSPlayerPawn pawn,CCSBot bot,float now)
    {
        if(!_coverPeeks.TryGetValue(p.Slot,out var peek))return;
        bool allowed=pawn.EntityHandle.Raw==peek.Pawn&&(pawn.Flags&1)!=0&&pawn.MoveType!=MoveType_t.MOVETYPE_LADDER
            &&!bot.IsEnemyVisible&&!bot.IsFriendInTheWay&&!bot.IsWaitingBehindFriend
            &&MathF.Abs(AimMath.Wrap(pawn.EyeAngles.Y-peek.ViewYaw))<25;
        if(CoverPeekPolicy.Cancel(peek.Route,bot.PathIndex,Position(pawn),NavGoal(bot),allowed,now-peek.Started)||now>=peek.Until)
        {CancelCoverPeek(p.Slot,"route-complete-or-invalid");return;}
        var eye=Position(pawn)+new Vec(0,0,pawn.ViewOffset.Z);
        if(ClearSegment(eye,peek.Route.CheckPoint))CancelCoverPeek(p.Slot,"angle-revealed");
    }
    private void TryCoverPeek(CCSPlayerController p,CCSPlayerPawn pawn,CCSBot bot,Vec checkPoint,float now)
    {
        if(_coverPeeks.ContainsKey(p.Slot)||_navigationPlans.ContainsKey(p.Slot)||now<_peekNext.GetValueOrDefault(p.Slot))return;
        _peekNext[p.Slot]=now+1.2f;
        if(!MovementContext(p,pawn,bot,now).Peek||(pawn.Flags&1)==0||pawn.MoveType==MoveType_t.MOVETYPE_LADDER
            ||MathF.Sqrt(pawn.AbsVelocity.X*pawn.AbsVelocity.X+pawn.AbsVelocity.Y*pawn.AbsVelocity.Y)>30
            ||bot.IsFriendInTheWay||bot.IsWaitingBehindFriend||OtherViewOwner(p.Slot)
            ||(_soundPreaim.TryGetValue(p.Slot,out var sound)&&now<sound.Until))return;
        var origin=Position(pawn);var goal=NavGoal(bot);var forward=goal-origin;forward.Z=0;
        if(!float.IsFinite(forward.X+forward.Y+goal.Z)||forward.Length()<64)return;
        forward=Vec.Normalize(forward);
        var eye=origin+new Vec(0,0,pawn.ViewOffset.Z);
        if(ClearSegment(eye,checkPoint))return;
        float yaw=pawn.EyeAngles.Y*MathF.PI/180;var right=new Vec(MathF.Sin(yaw),-MathF.Cos(yaw),0);
        var candidates=new List<PeekCandidate>();
        foreach(float sign in new[]{-1f,1f})
        {
            // The injected action is lateral only. Check that same route, plus
            // extra clearance for acceleration and the native AI's small drift.
            var dest=origin+right*(28*sign);
            bool clear=BodyPathClear(origin,dest,pawn.ViewOffset.Z)
                &&BodyPathClear(origin,origin+right*(44*sign),pawn.ViewOffset.Z);
            // Friendly bodies can block a clear world trace. This reads teammates
            // only, never hidden enemies to decide which side to expose.
            foreach(var mate in EntityPlayers().Where(x=>x.PawnIsAlive&&x.TeamNum==p.TeamNum&&x.Slot!=p.Slot))
            {
                if(mate.PlayerPawn.Value is not {IsValid:true} mp||mp.AbsOrigin==null)continue;
                var mo=Position(mp);
                if(MathF.Abs(mo.Z-origin.Z)<72&&Vec.Distance(mo,(origin+dest)*.5f)<52)clear=false;
            }
            candidates.Add(new(dest,clear,clear,ClearSegment(dest+new Vec(0,0,pawn.ViewOffset.Z),checkPoint)));
        }
        var plan=CoverPeekPolicy.Plan(bot.PathIndex,origin,goal,checkPoint,true,true,candidates);
        if(plan is not {} route)return;
        bool rightward=Vec.Dot(route.Destination-origin,right)>0;
        long token=_humanController?.InjectUsercmd(p.Slot,(ulong)(rightward?PlayerButtons.Moveright:PlayerButtons.Moveleft),140)??-1;
        if(token<=0)return;
        // Per-lease expiration remains effective even if no later callback runs.
        ResetSilentMovement(p.Slot);
        _coverPeeks[p.Slot]=new(pawn.EntityHandle.Raw,route,token,now,now+.14f,pawn.EyeAngles.Y);
        _peekNext[p.Slot]=now+2.5f;
        WriteTelemetry(new{kind="cover_peek_start",tick=Server.TickCount,time=now,slot=p.Slot,path_index=bot.PathIndex,
            goal_x=goal.X,goal_y=goal.Y,target_x=checkPoint.X,target_y=checkPoint.Y,
            destination_x=route.Destination.X,destination_y=route.Destination.Y,duration_ms=140});
    }
}
