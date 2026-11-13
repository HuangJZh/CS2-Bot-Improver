using BotCombatPolicy;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BotAimImprover;
public partial class BotAimImprover
{
    private readonly Dictionary<int,RetakeShotWatchdog> _retakeShotWatchdogs=new();
    private readonly Dictionary<int,(uint Pawn,uint Weapon,uint Target,long Token,int Until)> _retakeShotPulses=new();
    private readonly Dictionary<int,float> _retakeEquipAt=new();
    private void ReleaseRetakePulse(int slot)
    {if(_retakeShotPulses.Remove(slot,out var pulse))_humanController?.CancelUsercmdInjection(slot,pulse.Token);}
    private void ClearRetakeFireRecovery()
    {
        foreach(var slot in _retakeShotPulses.Keys.ToArray())ReleaseRetakePulse(slot);
        _retakeShotWatchdogs.Clear();_retakeEquipAt.Clear();
    }
    private void MaintainRetakePulses()
    {
        foreach(var (slot,pulse) in _retakeShotPulses)
        {
            var p=EntityPlayerAtSlot(slot);
            if(Server.TickCount>=pulse.Until||!RetakeOwnsGoals||p is not {IsValid:true,PawnIsAlive:true}
                ||p.HasBeenControlledByPlayerThisRound||p.PlayerPawn.Raw!=pulse.Pawn
                ||p.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Raw!=pulse.Weapon
                ||p.PlayerPawn.Value?.Bot?.Enemy.Raw!=pulse.Target)ReleaseRetakePulse(slot);
        }
    }
    private void RecoverRetakeEquipment(CCSPlayerController p,CCSPlayerPawn pawn)
    {
        if(!IsRetakeBot(p)||_retakeEquipBest==null||pawn.Bot is not {} bot||pawn.IsDefusing||OtherViewOwner(p.Slot)
            ||pawn.WeaponServices is not {} services||services.ActiveWeapon.Value is {IsValid:true}
            ||Server.CurrentTime<_retakeEquipAt.GetValueOrDefault(p.Slot))return;
        _retakeEquipAt[p.Slot]=Server.CurrentTime+1;
        if(services.MyWeapons.Any(h=>h.Value is {IsValid:true} w&&HumanFireContext.Classify(w.DesignerName)!=AimWeaponClass.Other))
            _retakeEquipBest.Invoke(bot.Handle,true);
    }
    private void RecoverRetakeTrigger(CCSPlayerController p,CCSPlayerPawn pawn,CCSBot bot,FireGateDecision decision)
    {
        if(!IsRetakeBot(p))return;
        if(!_retakeShotWatchdogs.TryGetValue(p.Slot,out var watch))_retakeShotWatchdogs[p.Slot]=watch=new();
        var gun=pawn.WeaponServices?.ActiveWeapon.Value?.As<CCSWeaponBase>();
        float now=Server.CurrentTime;
        bool ready=decision.Allow&&gun is {IsValid:true,Clip1:>0}&&!pawn.IsDefusing
            &&!Schema.GetRef<bool>(gun.Handle,"CCSWeaponBase","m_bInReload")&&gun.NextPrimaryAttackTick<=Server.TickCount
            &&new CCSPlayer_WeaponServices(pawn.WeaponServices!.Handle).NextAttack<=now
            &&_humanStates.TryGetValue(p.Slot,out var state)&&state.LastSight&&now-state.LastUsed<=.05f
            &&(HumanFireContext.Classify(gun.DesignerName)!=AimWeaponClass.Sniper||pawn.IsScoped);
        if(!ready)ReleaseRetakePulse(p.Slot);
        float shot=_humanStates.TryGetValue(p.Slot,out var history)?history.LastShotAt:float.NegativeInfinity;
        if(watch.Update(p.PlayerPawn.Raw,pawn.WeaponServices?.ActiveWeapon.Raw??uint.MaxValue,bot.Enemy.Raw,now,ready,shot)
            &&!_retakeShotPulses.ContainsKey(p.Slot))
        {
            long token=_humanController!.InjectUsercmd(p.Slot,(ulong)PlayerButtons.Attack,40);
            if(token>0)
            {
                _retakeShotPulses[p.Slot]=(p.PlayerPawn.Raw,pawn.WeaponServices!.ActiveWeapon.Raw,bot.Enemy.Raw,token,Server.TickCount+2);
                if(_telemetry!=null)WriteTelemetry(new{kind="retake_trigger_recovery",time=now,slot=p.Slot,pawn=p.PlayerPawn.Raw,weapon=pawn.WeaponServices.ActiveWeapon.Raw,target=bot.Enemy.Raw});
                Logger.LogDebug("Retake trigger recovery slot={Slot}: authorized target, no shot progress",p.Slot);
            }
        }
    }
}
