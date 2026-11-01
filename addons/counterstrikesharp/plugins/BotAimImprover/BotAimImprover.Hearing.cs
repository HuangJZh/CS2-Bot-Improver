using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System.Numerics;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private readonly Dictionary<int,HeardSound> _soundPreaim=new();
    private readonly Dictionary<uint,float> _lastFootstep=new();
    private readonly Dictionary<int,float> _attentionTurnAt=new();

    private bool HasReliableVisibleThreat(CCSPlayerController listener,float now)
    {
        var pawn=listener.PlayerPawn.Value;var bot=pawn?.Bot;
        return pawn is {IsValid:true} && bot is not null && bot.IsEnemyVisible && pawn.BlindUntilTime<=now
            && bot.Enemy.Value is {IsValid:true,LifeState:0} enemy && enemy.TeamNum!=listener.TeamNum
            && _humanStates.TryGetValue(listener.Slot,out var s) && s.Pawn==listener.PlayerPawn.Raw
            && s.Enemy==bot.Enemy.Raw && s.LastSight && now-s.Model.LastVisible<.20f;
    }
    private void QueueHeardSound(CCSPlayerController listener,CCSPlayerController source,Vector3 position,
        float distance,float now,float delay,string kind,float errorScale,float duration)
    {
        _soundPreaim.TryGetValue(listener.Slot,out var previous);
        float error=(1f+HumanAimProfile.For(_humanDifficulty).PerceptionError*12f)*errorScale;
        var angularError=previous is not null && previous.Until>now && previous.SourcePawn==source.PlayerPawn.Raw
            && previous.Gunshot==(kind=="gunfire") ? previous.Error
            : new Vector2((Random.Shared.NextSingle()*2-1)*error*.6f,(Random.Shared.NextSingle()*2-1)*error);
        var pawn=listener.PlayerPawn.Value!;var origin=pawn.AbsOrigin!;
        var eye=new Vector3(origin.X,origin.Y,origin.Z+pawn.ViewOffset.Z);
        var estimate=AimMath.Ray(eye,AimMath.Normalize(AimMath.Angles(eye,position)+angularError),distance);
        var incoming=new HeardSound(source.PlayerPawn.Raw,kind,estimate,distance,now,now+delay,
            now+Math.Clamp(duration,.5f,1.2f),angularError);
        var chosen=SoundAttention.Merge(previous,incoming);
        _soundPreaim[listener.Slot]=chosen;
        WriteTelemetry(new {kind="sound_preaim",tick=Server.TickCount,time=now,slot=listener.Slot,
            source=kind,source_pawn=source.PlayerPawn.Raw,distance,error_deg=error,
            start_delay=chosen.Start-now,accepted=ReferenceEquals(chosen,previous)==false});
    }
    private void HearSound(CCSPlayerController source,float radius,float duration,string kind,float delay,float errorScale)
    {
        if(!HumanActive || source is not {IsValid:true,PawnIsAlive:true} || source.TeamNum is not (2 or 3)
            || source.PlayerPawn.Value is not {IsValid:true} pawn || pawn.AbsOrigin is not {} origin
            || !float.IsFinite(radius+duration) || radius<=0 || duration<=0)return;
        float now=Server.CurrentTime;
        var position=new Vector3(origin.X,origin.Y,origin.Z+pawn.ViewOffset.Z);
        foreach(var listener in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if(listener is not {IsValid:true,IsBot:true,PawnIsAlive:true} || listener.TeamNum==source.TeamNum
                || listener.TeamNum is not (2 or 3) || listener.HasBeenControlledByPlayerThisRound
                || listener.PlayerPawn.Value is not {IsValid:true} lp || lp.AbsOrigin is not {} lo
                || HasReliableVisibleThreat(listener,now))continue;
            float distance=Vector3.Distance(position,new(lo.X,lo.Y,lo.Z+lp.ViewOffset.Z));
            if(distance<=radius)QueueHeardSound(listener,source,position,distance,now,delay,kind,errorScale,duration);
        }
    }
    private void HearFootstep(CCSPlayerController walker,float radius,float duration,string eventSource)
    {
        if(!HumanActive || walker is not {IsValid:true,PawnIsAlive:true})return;
        float now=Server.CurrentTime;uint pawn=walker.PlayerPawn.Raw;
        if(_lastFootstep.TryGetValue(pawn,out var last) && now-last<.08f)return;
        _lastFootstep[pawn]=now;
        WriteTelemetry(new {kind="sound_input",source=eventSource,step=true,time=now,tick=Server.TickCount,
            source_pawn=pawn,radius,duration});
        HearSound(walker,radius,duration,"footstep",.15f+Random.Shared.NextSingle()*.15f,1.25f);
    }
    private float AttentionDelta(int slot,float now)
    {
        float dt=_attentionTurnAt.TryGetValue(slot,out var previous) ? Math.Clamp(now-previous,0,.05f) : Server.TickInterval;
        _attentionTurnAt[slot]=now;
        return dt;
    }
}
