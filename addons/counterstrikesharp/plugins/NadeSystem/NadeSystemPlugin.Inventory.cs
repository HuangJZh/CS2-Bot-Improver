using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using System;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace NadeSystem;

public partial class NadeSystemPlugin
{
    private int _inventoryRound;
    private static string? InventoryItem(string type, int team) => type switch
    {
        "smoke" => "weapon_smokegrenade",
        "flash" => "weapon_flashbang",
        "he" => "weapon_hegrenade",
        "molotov" or "incgrenade" => team == 3 ? "weapon_incgrenade" : "weapon_molotov",
        _ => null // The original decoy replay is a repeating flash simulation, not a real decoy.
    };
    private static CBasePlayerWeapon? FindInventoryGrenade(CCSPlayerController bot, string type)
    {
        var item = InventoryItem(type, bot.TeamNum);
        return item == null ? null : bot.PlayerPawn.Value?.WeaponServices?.MyWeapons
            .Select(w => w.Value).FirstOrDefault(w => w is { IsValid: true } && w.DesignerName == item);
    }
    private static bool HasInventoryGrenade(CCSPlayerController bot, string type)
        => bot is { IsValid:true, IsBot:true, PawnIsAlive:true } && FindInventoryGrenade(bot,type) != null;

    private static void ConsumeInventoryGrenade(CCSPlayerController bot, string type)
    {
        var pawn=bot.PlayerPawn.Value;
        var weapon=FindInventoryGrenade(bot,type);
        if(pawn == null || weapon == null) return;
        var ammoType=weapon.As<CCSWeaponBase>().VData?.PrimaryAmmoType ?? -1;
        var services=pawn.WeaponServices!;
        if(ammoType >= 0 && ammoType < services.Ammo.Length && services.Ammo[ammoType] > 1)
        {
            services.Ammo[ammoType]--;
            Utilities.SetStateChanged(pawn,"CBasePlayerPawn","m_pWeaponServices");
            return;
        }
        if(ammoType >= 0 && ammoType < services.Ammo.Length) services.Ammo[ammoType]=0;
        pawn.RemovePlayerItem(weapon);
        weapon.Remove();
        Utilities.SetStateChanged(pawn,"CBasePlayerPawn","m_pWeaponServices");
    }

    // Purchase once at freeze end after the normal gun/armor buying phase.
    // Grenades persist in the actual inventory across rounds and on death/drop.
    private void BuyRoundGrenades()
    {
        if(_botNadesMode == "off") return;
        var rules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if(rules == null || rules.WarmupPeriod || rules.BuyTimeEnded) return;
        int limit=Math.Clamp(ConVar.Find("ammo_grenade_limit_total")?.GetPrimitiveValue<int>() ?? 4,0,4);
        int eligible=0,bought=0;
        foreach(var bot in Utilities.GetPlayers())
        {
            if(bot is not { IsValid:true, IsBot:true, PawnIsAlive:true } || bot.HasBeenControlledByPlayerThisRound
                || bot.TeamNum is not (2 or 3) || bot.PlayerPawn.Value is not { IsValid:true } pawn
                || pawn.WeaponServices == null || bot.InGameMoneyServices == null) continue;
            var money=bot.InGameMoneyServices;
            bool isCT=bot.TeamNum==3;
            int budget=GetRoundSpendCap(isCT,money.Account < 2800);
            var types=new[] { "smoke","flash","he","molotov" }.OrderBy(_ => Random.Shared.Next()).ToArray();
            int total=0;
            foreach(var handle in pawn.WeaponServices.MyWeapons)
            {
                var w=handle.Value;
                if(w is not { IsValid:true } || w.As<CCSWeaponBase>().VData?.GearSlot != gear_slot_t.GEAR_SLOT_GRENADES) continue;
                int ammo=w.As<CCSWeaponBase>().VData?.PrimaryAmmoType ?? -1;
                total += ammo >= 0 && ammo < pawn.WeaponServices.Ammo.Length ? Math.Max(1,(int)pawn.WeaponServices.Ammo[ammo]) : 1;
            }
            foreach(var type in types)
            {
                if(total >= limit) break;
                eligible++;
                if(FindInventoryGrenade(bot,type) != null) continue;
                int cost=(isCT ? CostCT : CostT)[type];
                if(money.Account < cost || budget < cost) continue;
                int before=money.Account;
                money.Account-=cost;
                try
                {
                    if(bot.GiveNamedItem(InventoryItem(type,bot.TeamNum)!) == nint.Zero)
                        throw new InvalidOperationException("Grenade purchase failed");
                }
                catch(Exception error)
                {
                    money.Account=before;
                    Server.PrintToConsole($"[NadeSystem] Purchase failed: {error.Message}");
                    continue;
                }
                budget-=cost;
                total++; bought++;
                Utilities.SetStateChanged(bot,"CCSPlayerController","m_pInGameMoneyServices");
                Server.PrintToConsole($"[NadeSystem] Purchased {type} for {bot.PlayerName}, cost={cost}");
                Logger.LogInformation("[NadeSystem] Purchased {Type} for {Bot}, cost={Cost}", type, bot.PlayerName, cost);
            }
        }
        if(bought>0 || eligible>0) Logger.LogInformation("[NadeSystem] Buy pass freezetime={Freeze} buyTimeEnded={Ended} eligible={Eligible} bought={Bought} mode={Mode}",rules.FreezePeriod,rules.BuyTimeEnded,eligible,bought,_botNadesMode);
    }
}
