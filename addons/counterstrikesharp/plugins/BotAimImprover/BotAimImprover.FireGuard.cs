using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private sealed record FireGuard(uint Pawn,long Token);
    private sealed record PendingFireGuardRelease(int Slot,uint Pawn,long Token,float Until,int Attempts);
    private readonly Dictionary<int,FireGuard> _fireGuards=new();
    private sealed record WeaponTurnClock(uint Pawn,uint Weapon,int BeforeTick,float BeforeFraction,float BeforeAttack,int UntilTick,float UntilAttack);
    private readonly Dictionary<int,WeaponTurnClock> _weaponTurnClocks=new();
    private readonly List<PendingFireGuardRelease> _pendingGuardReleases=new();
    // The model may delay the trigger only around its own planned movement window.
    // Anything longer is treated as a failed plan and firing is handed back to the
    // native bot AI so a never-ready plan cannot silence a bot for the whole match.
    private float PlannedFireHoldLimit
    {
        get
        {
            var profile=HumanAimProfile.For(_humanDifficulty);
            return Math.Clamp(profile.Reaction+profile.FlickTime+.25f,.35f,.8f);
        }
    }
    private readonly record struct FireGateDecision(bool Allow,bool Hold,string Reason);
    private static bool FirstShotPending(HumanState state,float now)=>state.SprayShots==0
        ||now-state.LastShotAt>.35f||float.IsFinite(state.Cadence.PauseUntil)&&now>=state.Cadence.PauseUntil;
    private bool TryCancelFireGuard(int slot,long token,out bool cancelled)
    {
        cancelled=false;
        if(token<=0)return true;
        if(_humanController is null)return false;
        try
        {
            cancelled=_humanController.CancelUsercmdSuppression(slot,token);
            return true;
        }
        catch(Exception)
        {
            return false;
        }
    }
    private void ReleaseFireGuard(int slot,string reason="ownership-release")
    {
        ReleaseWeaponTurnClock(slot);
        if(!_fireGuards.Remove(slot,out var guard))return;
        bool cancelled=false;
        // Cancel before dropping ownership. If cancellation cannot be confirmed the
        // token is queued for retry, because a lost suppression token would block
        // that bot's trigger for the rest of the map.
        bool handled=TryCancelFireGuard(slot,guard.Token,out cancelled);
        if(guard.Token>0 && (!handled || !cancelled))
            _pendingGuardReleases.Add(new(slot,guard.Pawn,guard.Token,Server.CurrentTime+2,0));
        if(_telemetry!=null)WriteTelemetry(new {kind="fire_gate_transition",tick=Server.TickCount,time=Server.CurrentTime,slot,pawn=guard.Pawn,
            action="release",reason,cancelled=guard.Token>0 ? cancelled : (bool?)null});
    }
    private void HoldWeaponTurnClock(CCSPlayerController player)
    {
        var pawn=player.PlayerPawn.Value;
        if(pawn?.WeaponServices is not {} services||services.ActiveWeapon.Value?.As<CCSWeaponBase>() is not {IsValid:true} gun)return;
        var csServices=new CCSPlayer_WeaponServices(services.Handle);
        if(_weaponTurnClocks.TryGetValue(player.Slot,out var prior)&&(prior.Pawn!=player.PlayerPawn.Raw||prior.Weapon!=services.ActiveWeapon.Raw))
            ReleaseWeaponTurnClock(player.Slot);
        int before=gun.NextPrimaryAttackTick;float fraction=gun.NextPrimaryAttackTickRatio,attack=csServices.NextAttack;
        if(_weaponTurnClocks.TryGetValue(player.Slot,out var old))
        {
            if(before==old.UntilTick){before=old.BeforeTick;fraction=old.BeforeFraction;}
            if(MathF.Abs(attack-old.UntilAttack)<.001f)attack=old.BeforeAttack;
        }
        int until=Math.Max(gun.NextPrimaryAttackTick,Server.TickCount+3);
        float untilAttack=MathF.Max(csServices.NextAttack,Server.CurrentTime+.05f);
        gun.NextPrimaryAttackTick=until;gun.NextPrimaryAttackTickRatio=0;csServices.NextAttack=untilAttack;
        _weaponTurnClocks[player.Slot]=new(player.PlayerPawn.Raw,services.ActiveWeapon.Raw,before,fraction,attack,until,untilAttack);
    }
    private void ReleaseWeaponTurnClock(int slot)
    {
        if(!_weaponTurnClocks.Remove(slot,out var saved))return;
        var player=EntityPlayerAtSlot(slot);
        if(player is not {IsValid:true}||player.PlayerPawn.Raw!=saved.Pawn||player.PlayerPawn.Value?.WeaponServices is not {} services)return;
        var handle=services.MyWeapons.FirstOrDefault(w=>w.Raw==saved.Weapon);
        if(handle?.Value?.As<CCSWeaponBase>() is {IsValid:true} gun&&gun.NextPrimaryAttackTick==saved.UntilTick)
        {gun.NextPrimaryAttackTick=saved.BeforeTick;gun.NextPrimaryAttackTickRatio=saved.BeforeFraction;}
        var csServices=new CCSPlayer_WeaponServices(services.Handle);
        if(services.ActiveWeapon.Raw==saved.Weapon&&MathF.Abs(csServices.NextAttack-saved.UntilAttack)<.001f)
            csServices.NextAttack=saved.BeforeAttack;
    }
    private void RetryPendingGuardReleases()
    {
        if(_pendingGuardReleases.Count==0)return;
        for(int i=_pendingGuardReleases.Count-1;i>=0;i--)
        {
            var pending=_pendingGuardReleases[i];
            bool cancelled=false;
            bool handled=TryCancelFireGuard(pending.Slot,pending.Token,out cancelled);
            if(handled && cancelled){_pendingGuardReleases.RemoveAt(i);continue;}
            if(Server.CurrentTime>=pending.Until)
            {
                _pendingGuardReleases.RemoveAt(i);
                Logger.LogWarning("Dropping unconfirmed fire guard token {Token} on slot {Slot} after {Attempts} retries",
                    pending.Token,pending.Slot,pending.Attempts);
                continue;
            }
            _pendingGuardReleases[i]=pending with {Attempts=pending.Attempts+1};
        }
    }
    private void ApplyFireGuard(CCSPlayerController player,FireGateDecision decision)
    {
        int slot=player.Slot;
        if(_fireGuards.TryGetValue(slot,out var old) && old.Pawn!=player.PlayerPawn.Raw)ReleaseFireGuard(slot);
        if(decision.Allow)
        {
            // Only a real model allow (planned-shot) refreshes the latch. An
            // allow-latch must let the latch expire so burst discipline can engage.
            if(decision.Reason=="planned-shot" && _humanStates.TryGetValue(slot,out var allowed) && allowed.Pawn==player.PlayerPawn.Raw)
                allowed.AllowLatchUntil=Server.CurrentTime+.15f;
            ReleaseFireGuard(slot,decision.Reason);return;
        }
        // No live visual plan: release instead of holding. The previous "keep fire
        // prohibited until a valid plan exists" rule turned every partial/invisible
        // acquisition into an indefinite block (bots stopped firing in natural play).
        if(!decision.Hold)
        {
            // A broken plan cycle must not poison the next one; only the timeout latch
            // survives until the model actually aligns or the plan is rebuilt.
            if(decision.Reason!="hold-timeout" && _humanStates.TryGetValue(slot,out var released)
                && released.Pawn==player.PlayerPawn.Raw)
            {released.PlanHoldSince=null;released.PlanHoldTimedOut=false;released.Execution.ResetReadiness();}
            ReleaseFireGuard(slot,decision.Reason);return;
        }
        if(decision.Reason is "target-outside-fire-cone" or "turn-not-ready" or "native-reaction")HoldWeaponTurnClock(player);
        else ReleaseWeaponTurnClock(slot);
        if(_fireGuards.TryGetValue(slot,out var current) && current.Token>0)return;
        long token=_humanController!.StartUsercmdSuppression(slot,(ulong)PlayerButtons.Attack);
        // BotController binds PlayerRunCommand lazily on the first live movement
        // frame. Equip can arrive earlier; retain pending intent and retry rather
        // than disabling the model for the rest of the match.
        _fireGuards[slot]=new(player.PlayerPawn.Raw,Math.Max(0,token));
        if(_humanStates.TryGetValue(slot,out var state) && state.Pawn==player.PlayerPawn.Raw)
            state.PlanHoldSince??=Server.CurrentTime;
        if(_telemetry!=null && (token>0 || current==null))WriteTelemetry(new {kind="fire_gate_transition",tick=Server.TickCount,time=Server.CurrentTime,
            slot,pawn=player.PlayerPawn.Raw,action=token>0 ? "hold" : "pending",reason=decision.Reason});
    }
    private bool EligibleForFireGuard(CCSPlayerController player,CCSPlayerPawn pawn)=>HumanActive
        && player.IsValid && player.IsBot && player.PawnIsAlive && !player.HasBeenControlledByPlayerThisRound
        && IsGun(pawn) && !OtherViewOwner(player.Slot);
    // The guard may only hold the trigger while the model actually owns a live visual
    // plan for this pawn/target/weapon and is still before its planned shot. In every
    // other case returning (false,false,reason) releases the guard and lets the native
    // bot decide, so a plan that never becomes ready cannot silence the bot.
    private FireGateDecision EvaluateFireGate(CCSPlayerController player,CCSPlayerPawn pawn,CCSBot bot,bool advanceTimers)
    {
        if(TestGateActive)return new(false,true,"test-override");
        if(_humanSettings.NativeCombat)return EvaluateNativeCombatGate(player,pawn,bot);
        // Inhibit only: these coarse native-enemy coordinates never steer aim or
        // enter the perceptual model. Keep this check ahead of every fail-open path,
        // timeout and burst latch, including a newly assigned or retained enemy.
        if(bot.Enemy.Value is {IsValid:true,LifeState:0} nativeEnemy&&nativeEnemy.TeamNum!=player.TeamNum
            &&nativeEnemy.AbsOrigin is {} origin&&TryGetBotEyePosition(player,out var eye))
        {
            var bearing=AimMath.Angles(new(eye.X,eye.Y,eye.Z),new(origin.X,origin.Y,origin.Z+nativeEnemy.ViewOffset.Z*.7f));
            if(FacingFirePolicy.OutsideCone(pawn.EyeAngles.Y,bearing.Y))return new(false,true,"target-outside-fire-cone");
        }
        if(!_humanStates.TryGetValue(player.Slot,out var state))return new(false,false,"no-plan");
        if(state.Pawn!=player.PlayerPawn.Raw)return new(false,false,"pawn-changed");
        if(state.Enemy!=bot.Enemy.Raw)return new(false,false,"target-changed");
        if(bot.Enemy.Value is not {IsValid:true} enemy || enemy.LifeState!=0 || enemy.TeamNum==player.TeamNum)return new(false,false,"invalid-enemy");
        if(!state.LastSight || !bot.IsEnemyVisible || pawn.BlindUntilTime>Server.CurrentTime)return new(false,false,"not-visible");
        if(!state.Model.HasObservation)return new(false,false,"no-observation");
        if(!_modelSpots.ContainsKey(bot.Handle))return new(false,false,"no-visual-plan");
        if(state.PlannedWeapon!=(pawn.WeaponServices?.ActiveWeapon.Raw ?? uint.MaxValue))return new(false,false,"weapon-changed");
        float now=Server.CurrentTime;
        if(now-state.LastUsed>.05f || now-state.Model.LastVisible>.20f)return new(false,false,"stale-plan");
        // A short permission latch keeps a burst going after the model has already
        // authorized fire, instead of re-holding for a single misaligned tick and
        // forcing another Attack release edge in the middle of the trigger.
        // Human burst discipline: after the planned number of rounds, release the
        // trigger for a short human-like pause instead of holding a perfect spray.
        if(now<state.BurstCooldownUntil)return new(false,true,"burst-pause");
        if(state.Output.AllowFire||now<state.AllowLatchUntil)
        {
            if(advanceTimers){state.PlanHoldSince=null;state.PlanHoldTimedOut=false;}
            var weapon=HumanFireContext.Classify(pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName);
            float floor=weapon switch {AimWeaponClass.Sniper=>.30f,AimWeaponClass.PrecisePistol=>.40f,
                AimWeaponClass.Smg or AimWeaponClass.Shotgun=>1f,_=>.65f};
            float tolerance=Math.Clamp(MathF.Max(floor,(state.Model.LastFireDecision.ToleranceDegrees??.5f)+state.Model.Profile.MotorError*.5f),floor,3f);
            var delta=AimMath.Delta(state.Output.Angles,new(pawn.EyeAngles.X,pawn.EyeAngles.Y));
            float error=delta.Length();
            var permission=state.Execution.FirstShot(now,FirstShotPending(state,now),error,tolerance,advanceTimers,state.Model.Profile.FirstShotSettleLimit,delta.Y);
            if(advanceTimers)state.ExecutionPermission=permission;
            if(!permission.Allow)return new(false,true,permission.Reason);
            return new(true,false,permission.Reason=="execution-settle-timeout"?permission.Reason:
                state.Output.AllowFire?"planned-shot":"allow-latch");
        }
        if(state.PlanHoldTimedOut)return new(false,false,"hold-timeout");
        if(state.PlanHoldSince is {} since && now-since>PlannedFireHoldLimit)
        {
            if(advanceTimers)state.PlanHoldTimedOut=true;
            return new(false,false,"hold-timeout");
        }
        if(advanceTimers)state.PlanHoldSince??=now;
        return new(false,true,state.Model.LastFireDecision.Reason is {Length:>0} reason ? reason : "planning");
    }
    private void RefreshFireGuard(CCSPlayerController player)
    {
        var pawn=player.PlayerPawn.Value;
        if(pawn is not {IsValid:true} || !EligibleForFireGuard(player,pawn) || pawn.Bot is not {} bot)
        {ReleaseFireGuard(player.Slot);return;}
        ApplyFireGuard(player,EvaluateFireGate(player,pawn,bot,true));
    }
    private void RefreshNativeFireGuard(DynamicHook hook)
    {
        if(!HumanActive)return;
        try
        {
            var player=ResolveBotController(hook.GetParam<nint>(0));
            if(player is {IsValid:true})RefreshFireGuard(player);
        }
        catch(Exception error)
        {
            _humanSettings.Enabled=false;ResetHuman();
            Logger.LogError(error,"Human aim disabled after native fire guard failure");
        }
    }
    private void MaintainFireGuards()
    {
        try
        {
            RetryPendingGuardReleases();
            if(!HumanActive)
            {
                foreach(int slot in _fireGuards.Keys.ToArray())ReleaseFireGuard(slot);
                return;
            }
            // ReleaseFireGuard only removes; safe during .NET 10 enumeration.
            foreach(var (slot,guard) in _fireGuards)
            {
                var player=EntityPlayerAtSlot(slot);
                if(player is not {IsValid:true,IsBot:true,PawnIsAlive:true} || player.HasBeenControlledByPlayerThisRound
                    || player.PlayerPawn.Raw!=guard.Pawn)ReleaseFireGuard(slot);
            }
            foreach(var player in EntityPlayers())
                if(player is {IsValid:true,IsBot:true})RefreshFireGuard(player);
        }
        catch(Exception error)
        {
            _humanSettings.Enabled=false;ResetHuman();
            Logger.LogError(error,"Human aim disabled after fire guard maintenance failure");
        }
    }
}
