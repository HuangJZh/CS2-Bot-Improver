using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotState;

public partial class BotState
{
    private float _openingSprintUntil;
    private readonly Dictionary<int,uint> _openingKnives=new();
    private readonly HashSet<int> _openingStopped=new();

    private void BeginOpeningSprint()
    {
        EndOpeningSprint();
        var rules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if(_nativeCombat||RetakeOwnsGoals||rules is null||rules.WarmupPeriod
            ||ConVar.Find("game_type")?.GetPrimitiveValue<int>()!=0
            ||ConVar.Find("game_mode")?.GetPrimitiveValue<int>() is not (0 or 1))return;
        _openingSprintUntil=Server.CurrentTime+4;
    }
    private void EndOpeningSprint()
    {
        _openingSprintUntil=0;
        foreach(int slot in _openingKnives.Keys.ToArray())ReleaseOpeningKnife(slot);
        _openingStopped.Clear();
    }
    private void MaintainOpeningSprint()
    {
        foreach(var (slot,pawn) in _openingKnives.ToArray())
        {
            var p=Utilities.GetPlayerFromSlot(slot);
            if(Server.CurrentTime>=_openingSprintUntil||p is not {IsValid:true,IsBot:true,PawnIsAlive:true}
                ||p.PlayerPawn.Raw!=pawn||p.HasBeenControlledByPlayerThisRound)ReleaseOpeningKnife(slot);
        }
    }
    private void ReleaseOpeningKnife(int slot)
    {
        if(!_openingKnives.Remove(slot,out uint pawnId)||_botController==null)return;
        if(BotControllerBridge.IsKnifeLocked(_botController,slot))BotControllerBridge.UnlockWeapon(_botController,slot);
        var p=Utilities.GetPlayerFromSlot(slot);
        if(p is not {IsValid:true,IsBot:true,PawnIsAlive:true}||p.HasBeenControlledByPlayerThisRound
            ||p.PlayerPawn.Raw!=pawnId||p.PlayerPawn.Value is not {IsValid:true} pawn)return;
        if(pawn.WeaponServices?.ActiveWeapon.Value?.As<CCSWeaponBase>().VData?.GearSlot==gear_slot_t.GEAR_SLOT_KNIFE
            &&GetReequipWeapon(pawn,out int def))BotControllerBridge.SwitchBotWeapon(_botController,slot,def);
    }
    private void StopOpeningSprint(int slot)
    {
        _openingStopped.Add(slot);
        ReleaseOpeningKnife(slot);
    }
    private void TickOpeningSprint(CCSPlayerController p,CCSPlayerPawn pawn,CCSBot bot,float now)
    {
        int slot=p.Slot;
        // The feature costs no inventory/schema reads outside its four seconds.
        if(now>=_openingSprintUntil||_nativeCombat||RetakeOwnsGoals||_openingStopped.Contains(slot))
        {if(_openingKnives.ContainsKey(slot))ReleaseOpeningKnife(slot);return;}
        var active=pawn.WeaponServices?.ActiveWeapon.Value?.As<CCSWeaponBase>();
        var gear=active?.VData?.GearSlot;
        bool busy=gear is not (gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL or gear_slot_t.GEAR_SLOT_KNIFE)
            ||pawn.IsDefusing||pawn.BlindUntilTime>now||bot.IsEnemyVisible||bot.IsAttacking;
        if(busy||_botController==null||p.HasBeenControlledByPlayerThisRound)
        {if(_openingKnives.ContainsKey(slot))StopOpeningSprint(slot);else if(busy&&now<_openingSprintUntil)_openingStopped.Add(slot);return;}
        if(_openingKnives.TryGetValue(slot,out var owner))
        {
            if(owner!=p.PlayerPawn.Raw||!BotControllerBridge.IsKnifeLocked(_botController,slot))StopOpeningSprint(slot);
            else bot.IsRunning=true;
            return;
        }
        // Never replace a weapon/replay lock belonging to another feature.
        if(BotControllerBridge.IsWeaponControlled(_botController,slot)||BotControllerBridge.IsViewControlled(_botController,slot)
            ||!GetReequipWeapon(pawn,out _))return;
        if(!BotControllerBridge.SwitchBotWeapon(_botController,slot,KnifeDefinitionIndex))return;
        if(BotControllerBridge.LockKnife(_botController,slot))
        {_openingKnives[slot]=p.PlayerPawn.Raw;bot.IsRunning=true;}
    }
}
