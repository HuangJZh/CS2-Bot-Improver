using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System.Text.Json;
using Microsoft.Extensions.Logging;
namespace BotAimImprover;
public partial class BotAimImprover
{
    private StreamWriter? _telemetry;
    private CCSGameRules? _telemetryRules;
    private float _telemetryUntil,_telemetryFlush;
    private int _telemetryTick=-1;
    private string? _telemetryPath;
    private void InitializeAimTelemetry()
    {
        RegisterListener<Listeners.OnTick>(() => {
            if(_telemetry == null || _telemetryTick==Server.TickCount)return;
            _telemetryTick=Server.TickCount;
            if(Server.CurrentTime>=_telemetryUntil || _telemetry.BaseStream.Position>100_000_000) { StopAimTelemetry();return; }
            foreach(var player in Utilities.GetPlayers())
                if(player is {IsValid:true,IsBot:true,PawnIsAlive:true} && !player.HasBeenControlledByPlayerThisRound)
                    RecordAimTelemetry("server_tick_observation",player);
            if(Server.CurrentTime>=_telemetryFlush)
            {
                try { _telemetry?.Flush();_telemetryFlush=Server.CurrentTime+1; }
                catch(IOException error) { StopAimTelemetry();Logger.LogWarning(error,"Aim capture stopped after flush failure"); }
            }
        });
        RegisterEventHandler<EventWeaponFire>((e,i) => { if(e.Userid is {} p)RecordAimTelemetry("weapon_fire_callback",p,e.Weapon);return HookResult.Continue; },HookMode.Post);
        RegisterEventHandler<EventPlayerHurt>((e,i) => { if(e.Attacker is {} p)RecordAimTelemetry("player_hurt_callback",p,e.Weapon,e.Userid?.PlayerPawn.Raw,e.DmgHealth);return HookResult.Continue; },HookMode.Post);
        RegisterEventHandler<EventPlayerDeath>((e,i) => { if(e.Userid is {} p)RecordAimTelemetry("player_death_callback",p,null,e.Attacker?.PlayerPawn.Raw);return HookResult.Continue; },HookMode.Post);
        RegisterEventHandler<EventRoundStart>((e,i) => { WriteTelemetry(new {kind="round_boundary",tick=Server.TickCount,time=Server.CurrentTime});return HookResult.Continue; });
        RegisterListener<Listeners.OnMapEnd>(StopAimTelemetry);
        AddCommand("bot_aim_capture","Local console: start [seconds=120], stop, status",(caller,c) => {
            if(caller != null)return;
            var mode=c.ArgCount>1 ? c.GetArg(1).ToLowerInvariant() : "status";
            if(mode=="stop")StopAimTelemetry();
            if(mode=="start")
            {
                StopAimTelemetry();
                int seconds=c.ArgCount>2 && int.TryParse(c.GetArg(2),out var n) ? Math.Clamp(n,10,300) : 120;
                try
                {
                    _nativePhaseCalls.Clear();
                    var directory=Path.Combine(ModuleDirectory,"telemetry");Directory.CreateDirectory(directory);
                    _telemetryPath=Path.Combine(directory,$"aim-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
                    _telemetry=new StreamWriter(new FileStream(_telemetryPath,FileMode.CreateNew,FileAccess.Write,FileShare.Read),new System.Text.UTF8Encoding(false),65536);
                    _telemetryRules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault(p=>p.IsValid)?.GameRules;
                    _telemetryUntil=Server.CurrentTime+seconds;_telemetryTick=-1;
                    WriteTelemetry(new {kind="session",schema="bot-aim-telemetry-v1",map=Server.MapName,difficulty=_humanDifficulty,model_active=HumanActive,module_version=ModuleVersion,direct_view_requested=_humanSettings.DirectViewControl,direct_view_available=_setEyeAngles!=null,tick_interval=Server.TickInterval,
                        native_phase_ready=_nativePhasesReady,note="Tick observations and event-callback angles; not raw usercmd or exact subtick shot angles. Captures bots only. No network upload."});
                }
                catch(Exception error) { StopAimTelemetry();Logger.LogError(error,"Cannot start aim capture"); }
            }
            Server.PrintToConsole($"[AimCapture] active={_telemetry!=null} path={_telemetryPath}");
        });
    }
    private void RecordAimTelemetry(string kind,CCSPlayerController player,string? eventWeapon=null,uint? otherPawn=null,int? damage=null)
    {
        if(_telemetry == null || !player.IsValid || !player.IsBot || player.HasBeenControlledByPlayerThisRound)return;
        var pawn=player.PlayerPawn.Value;if(pawn is not {IsValid:true})return;
        _humanStates.TryGetValue(player.Slot,out var state);
        CCSPlayer_AimPunchServices? punch=pawn.AimPunchServices;
        if(punch?.Handle==nint.Zero)punch=null;
        var angles=pawn.EyeAngles;var pos=pawn.AbsOrigin;var vel=pawn.AbsVelocity;var bot=pawn.Bot;
        var gun=pawn.WeaponServices?.ActiveWeapon.Value?.As<CCSWeaponBase>();
        var csWeaponServices=pawn.WeaponServices is {} services ? new CCSPlayer_WeaponServices(services.Handle) : null;
        FireGateDecision? gate=bot!=null && EligibleForFireGuard(player,pawn) ? EvaluateFireGate(player,pawn,bot,false) : null;
        // Enemy world state is recorded for offline analysis only; the human aim
        // model never receives it (it only consumes its own delayed visual samples).
        CCSPlayerPawn? enemyPawn=null;
        try{enemyPawn=bot?.Enemy.Value;}catch(Exception){enemyPawn=null;}
        Vector? enemyOrigin=enemyPawn is {IsValid:true} ep1 ? ep1.AbsOrigin : null;
        Vector? enemyVelocity=enemyPawn is {IsValid:true} ep2 ? ep2.AbsVelocity : null;
        float? enemyDistance=null;
        if(enemyOrigin!=null && pos!=null)
        {
            float dx=enemyOrigin.X-pos.X,dy=enemyOrigin.Y-pos.Y,dz=enemyOrigin.Z-pos.Z;
            enemyDistance=MathF.Sqrt(dx*dx+dy*dy+dz*dz);
        }
        WriteTelemetry(new {kind,tick=Server.TickCount,time=Server.CurrentTime,slot=player.Slot,pawn=player.PlayerPawn.Raw,alive=player.PawnIsAlive,
            game_warmup=_telemetryRules?.WarmupPeriod,game_freeze=_telemetryRules?.FreezePeriod,rounds_played=_telemetryRules?.TotalRoundsPlayed,
            x=pos?.X,y=pos?.Y,z=pos?.Z,pitch=angles.X,yaw=angles.Y,vx=vel.X,vy=vel.Y,vz=vel.Z,flags=pawn.Flags,
            weapon=pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName,scoped=pawn.IsScoped,
            weapon_clip=gun?.Clip1,weapon_next_primary_tick=gun?.NextPrimaryAttackTick,
            weapon_next_primary_fraction=gun!=null && float.IsFinite(gun.NextPrimaryAttackTickRatio) ? (float?)gun.NextPrimaryAttackTickRatio : null,
            player_next_attack_time=csWeaponServices!=null && float.IsFinite(csWeaponServices.NextAttack) ? (float?)csWeaponServices.NextAttack : null,
            view_offset_z=pawn.ViewOffset.Z,native_crouching=bot?.IsCrouching,native_attacking=bot?.IsAttacking,
            shots_fired=pawn.ShotsFired,
            punch_predictable_pitch=punch?.PredictableBaseAngle.X,punch_predictable_yaw=punch?.PredictableBaseAngle.Y,
            punch_predictable_vel_pitch=punch?.PredictableBaseAngleVel.X,punch_predictable_vel_yaw=punch?.PredictableBaseAngleVel.Y,
            punch_predictable_tick=punch?.PredictableBaseTick,punch_predictable_tick_interp=punch?.PredictableBaseTickInterpAmount,
            punch_unpredictable_pitch=punch?.UnpredictableBaseAngle.X,punch_unpredictable_yaw=punch?.UnpredictableBaseAngle.Y,punch_unpredictable_tick=punch?.UnpredictableBaseTick,
            native_look_pitch=bot?.LookPitch,native_look_yaw=bot?.LookYaw,
            native_goal_pitch=bot?.AimGoal.X,native_goal_yaw=bot?.AimGoal.Y,
            native_look_at_x=bot?.LookAtSpot.X,native_look_at_y=bot?.LookAtSpot.Y,native_look_at_z=bot?.LookAtSpot.Z,native_look_at_attack=bot?.LookAtSpotAttack,
            native_target_x=bot?.TargetSpot.X,native_target_y=bot?.TargetSpot.Y,native_target_z=bot?.TargetSpot.Z,
            enemy_x=enemyOrigin?.X,enemy_y=enemyOrigin?.Y,enemy_z=enemyOrigin?.Z,
            enemy_vx=enemyVelocity?.X,enemy_vy=enemyVelocity?.Y,enemy_vz=enemyVelocity?.Z,enemy_distance=enemyDistance,
            contact_gap_degrees=state!=null && float.IsFinite(state.Model.ContactGapDegrees) ? (float?)state.Model.ContactGapDegrees : null,
            contact_kind=state?.Model.ContactKind,sample_result=state?.LastSampleResult,
            sample_attempts=state?.LastSampleAttempts,sample_point=state?.LastSamplePoint,
            sample_ray_fraction=state?.LastSampleRayFraction,
            model_plan_start_time=state is {} planState && planState.Model.ContactKind!="none" ? (float?)planState.Model.PlanStartedAt : null,
            model_plan_duration=state is {} durationState && durationState.Model.ContactKind!="none" ? (float?)durationState.Model.PlanDuration : null,
            native_phase=bot!=null ? NativePhase(bot.Handle) : null,
            native_fire_weapon_timestamp=bot?.FireWeaponTimestamp,
            model_last_used_time=state?.LastUsed,
            model_first_visual_time=state?.FirstVisualAt,native_guard_reason=gate?.Reason,native_guard_hold=gate?.Hold,
            enemy=bot?.Enemy.Raw,native_visible=bot?.IsEnemyVisible,sensor_visible=state?.LastSight,
            planned_pitch=state?.Output.Angles.X,planned_yaw=state?.Output.Angles.Y,phase=state?.Output.Phase,allow_fire=state?.Output.AllowFire,
            observation_age=state!=null && float.IsFinite(state.Output.ObservationAge) ? (float?)state.Output.ObservationAge : null,
            visual_uncertainty_degrees=state?.LastVisualEstimate?.UncertaintyDegrees,
            visual_eccentricity_degrees=state?.LastVisualEstimate?.EccentricityDegrees,
            visual_angular_radius_degrees=state?.LastVisualEstimate?.AngularRadiusDegrees,
            fire_reason=state?.Model.LastFireDecision.Reason,
            fire_expected_error_degrees=state?.Model.LastFireDecision.ExpectedErrorDegrees,
            fire_tolerance_degrees=state?.Model.LastFireDecision.ToleranceDegrees,
            under_fire=state!=null && Server.CurrentTime<state.UnderFireUntil,
            allow_latch_active=state!=null && Server.CurrentTime<state.AllowLatchUntil,
            model_memory_aim=state is {} memoryState && !memoryState.LastSight && memoryState.Model.HasObservation
                && Server.CurrentTime-memoryState.Model.LastVisible<=ReacquireResetSeconds,
            test_gate_active=TestGateActive,effective_allow_fire=gate?.Allow,
            fire_guard_hold_since=state?.PlanHoldSince,fire_guard_hold_timed_out=state?.PlanHoldTimedOut,
            model_fire_suppression_owned=_fireGuards.TryGetValue(player.Slot,out var guard) && guard.Pawn==player.PlayerPawn.Raw && guard.Token>0,
            fire_guard_pending=guard!=null && guard.Pawn==player.PlayerPawn.Raw && guard.Token<=0,
            event_weapon=eventWeapon,other_pawn=otherPawn,damage});
    }
    private void WriteTelemetry<T>(T row)
    {
        if(_telemetry == null)return;
        try { _telemetry.WriteLine(JsonSerializer.Serialize(row)); }
        catch(Exception error) { StopAimTelemetry();Logger.LogWarning(error,"Aim capture stopped after write failure"); }
    }
    private void StopAimTelemetry()
    {
        var writer=_telemetry;_telemetry=null;_telemetryRules=null;
        if(writer!=null)
        {
            try{writer.WriteLine(JsonSerializer.Serialize(new {kind="session_end",tick=Server.TickCount,time=Server.CurrentTime,native_phase_calls=_nativePhaseCalls}));}
            catch(IOException){}
        }
        try { writer?.Dispose(); } catch(IOException) { }
    }
}
