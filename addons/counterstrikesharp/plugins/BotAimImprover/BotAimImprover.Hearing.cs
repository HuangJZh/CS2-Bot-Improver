using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System.Numerics;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private readonly Dictionary<int,HeardSound> _soundPreaim=new();
    private readonly Dictionary<uint,float> _lastFootstep=new();
    private readonly Dictionary<int,float> _attentionTurnAt=new();
    private sealed record VisualAttention(uint Pawn,uint Target,float Until,float AlertCredit);
    private sealed record VisibilityScan(uint Pawn,uint Enemy,int Tick,float Pitch,float Yaw,bool Visible);
    private sealed record SoundFocusOwner(uint Pawn,SoundGazeFocus Focus);
    private readonly Dictionary<int,VisualAttention> _visualAttention=new();
    private readonly Dictionary<int,VisibilityScan> _visibilityScans=new();
    private readonly Dictionary<int,SoundFocusOwner> _soundFocus=new();
    private readonly HashSet<nint> _soundTurnOwners=new();

    private bool HasReliableVisibleThreat(CCSPlayerController listener,float now)
    {
        var pawn=listener.PlayerPawn.Value;var bot=pawn?.Bot;
        if(pawn is not {IsValid:true}||bot is null)return false;
        if(_combatHistories.TryGetValue(listener.Slot,out var firing)&&firing.Pawn==listener.PlayerPawn.Raw
            &&now-firing.History.LastShotAt<.25f&&bot.Enemy.Value is {IsValid:true,LifeState:0})return true;
        if(pawn.BlindUntilTime>now){_visualAttention.Remove(listener.Slot);return false;}
        bool remembered=_visualAttention.TryGetValue(listener.Slot,out var memory)&&memory.Pawn==listener.PlayerPawn.Raw&&now<memory.Until;
        if(remembered&&memory!.Target==bot.Enemy.Raw&&bot.IsEnemyVisible)return true;
        var view=new Vector2(pawn.EyeAngles.X,pawn.EyeAngles.Y);
        if(_visibilityScans.TryGetValue(listener.Slot,out var cached)&&cached.Pawn==listener.PlayerPawn.Raw
            &&cached.Enemy==bot.Enemy.Raw&&cached.Tick==Server.TickCount
            &&AimMath.Delta(view,new(cached.Pitch,cached.Yaw)).Length()<2)return cached.Visible||remembered;
        bool seen=false;
        if(TryGetBotEyePosition(listener,out var eye))
        {
            var from=new Vector3(eye.X,eye.Y,eye.Z);
            // Strict FOV, smoke and world LOS checks are independent of the model's
            // delayed sample queue. Never turn toward these scans or force Enemy.
            foreach(var p in EntityPlayers().OrderByDescending(p=>p.PlayerPawn.Raw==bot.Enemy.Raw))
            {
                if(p is not {IsValid:true,PawnIsAlive:true}||p.TeamNum==listener.TeamNum||p.TeamNum is not (2 or 3)
                    ||p.PlayerPawn.Value is not {IsValid:true} target)continue;
                foreach(int part in new[]{0,3})
                {
                    if(!TryComputePartPos(target,part,out var x,out var y,out var z))continue;
                    var point=new Vector3(x,y,z);var delta=AimMath.Delta(AimMath.Angles(from,point),view);
                    if(MathF.Abs(delta.Y)>_humanSettings.HorizontalFieldOfView*.5f||MathF.Abs(delta.X)>50
                        ||HumanSmokeBlocks(eye,point)||!PointVisibleFromEye(eye,x,y,z,.999f,out _))continue;
                    ConfirmVisualAttention(listener,target.EntityHandle.Raw,now);
                    seen=true;break;
                }
                if(seen)break;
            }
        }
        _visibilityScans[listener.Slot]=new(listener.PlayerPawn.Raw,bot.Enemy.Raw,Server.TickCount,view.X,view.Y,seen);
        return seen||remembered;
    }
    private void ConfirmVisualAttention(CCSPlayerController listener,uint target,float now)
    {
        float credit=_visualAttention.TryGetValue(listener.Slot,out var old)&&old.Pawn==listener.PlayerPawn.Raw
            &&old.Target==target&&now<old.Until?old.AlertCredit:0;
        if(_soundPreaim.TryGetValue(listener.Slot,out var sound)&&sound.SourcePawn==target&&now>=sound.Start&&now<sound.Until)
            credit=MathF.Max(credit,now-sound.Start);
        _visualAttention[listener.Slot]=new(listener.PlayerPawn.Raw,target,now+.22f,credit);
    }
    private void CancelSoundSteering(CCSPlayerController p)
    {
        if(p.PlayerPawn.Value?.Bot is not {} bot||!_soundTurnOwners.Remove(bot.Handle))return;
        _nativeTurnAt.Remove(bot.Handle);_turnAssistActive.Remove(bot.Handle);
        if(_pendingLargeTurns.TryGetValue(bot.Handle,out var turn)&&turn.Auditory)_pendingLargeTurns.Remove(bot.Handle);
        _modelSpots.Remove(bot.Handle);
        if(!OtherViewOwner(p.Slot)&&p.PlayerPawn.Value is {IsValid:true} pawn)
        {bot.LookPitch=pawn.EyeAngles.X;bot.LookYaw=pawn.EyeAngles.Y;bot.LookPitchVel=0;bot.LookYawVel=0;}
        WriteTelemetry(new{kind="sound_gaze_release",time=Server.CurrentTime,slot=p.Slot});
    }
    private void CancelSoundAttention(CCSPlayerController p)
    {CancelSoundSteering(p);_soundPreaim.Remove(p.Slot);_soundFocus.Remove(p.Slot);}
    private void ForgetVisualTarget(uint pawn)
    {
        foreach(var slot in _visualAttention.Where(x=>x.Value.Target==pawn||x.Value.Pawn==pawn).Select(x=>x.Key).ToArray())
        {_visualAttention.Remove(slot);_visibilityScans.Remove(slot);}
    }
    private void ApplyVisibleAlertCredit(CCSPlayerController p,HumanState state,float now)
    {
        if(_visualAttention.TryGetValue(p.Slot,out var sight)&&sight.Pawn==p.PlayerPawn.Raw&&sight.Target==state.Enemy&&now<sight.Until)
            state.Model.CreditAlertReaction(sight.AlertCredit);
    }
    private void QueueHeardSound(CCSPlayerController listener,CCSPlayerController source,Vector3 position,
        float distance,float now,float delay,string kind,float errorScale,float duration)
    {
        _soundPreaim.TryGetValue(listener.Slot,out var previous);
        float error=(1f+HumanAimProfile.For(_humanDifficulty).PerceptionError*12f)*errorScale;
        var angularError=previous is not null && previous.Until>now && previous.SourcePawn==source.PlayerPawn.Raw ? previous.Error
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
        if(!HumanActive || _humanSettings.NativeCombat || source is not {IsValid:true,PawnIsAlive:true} || source.TeamNum is not (2 or 3)
            || source.PlayerPawn.Value is not {IsValid:true} pawn || pawn.AbsOrigin is not {} origin
            || !float.IsFinite(radius+duration) || radius<=0 || duration<=0)return;
        float now=Server.CurrentTime;
        var position=new Vector3(origin.X,origin.Y,origin.Z+pawn.ViewOffset.Z);
        foreach(var listener in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if(listener is not {IsValid:true,IsBot:true,PawnIsAlive:true} || listener.TeamNum==source.TeamNum
                || listener.TeamNum is not (2 or 3) || listener.HasBeenControlledByPlayerThisRound
                || listener.PlayerPawn.Value is not {IsValid:true} lp || lp.AbsOrigin is not {} lo||OtherViewOwner(listener.Slot)
                ||lp.IsDefusing||(_plantingUntil.TryGetValue(listener.Slot,out var planting)&&now<planting))continue;
            if(HasReliableVisibleThreat(listener,now)){CancelSoundAttention(listener);continue;}
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
