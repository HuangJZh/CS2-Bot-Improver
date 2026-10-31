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
        var rules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if(_botNadesMode == "off")
        {
            Logger.LogInformation("[NadeSystem] buy pass skipped mode=off");
            return;
        }
        if(rules == null || rules.WarmupPeriod || rules.BuyTimeEnded)
        {
            Logger.LogInformation("[NadeSystem] buy pass blocked rules={Rules} warmup={Warmup} buyTimeEnded={Ended} mode={Mode}",
                rules != null, rules?.WarmupPeriod == true, rules?.BuyTimeEnded == true, _botNadesMode);
            return;
        }
        int limit=Math.Clamp(ConVar.Find("ammo_grenade_limit_total")?.GetPrimitiveValue<int>() ?? 4,0,4);
        int players=0,notBot=0,dead=0,teamBad=0,pawnBad=0,controlled=0,weaponsNull=0,moneyNull=0;
        int bots=0,eligible=0,bought=0,noMoney=0,already=0;
        foreach(var bot in Utilities.GetPlayers())
        {
            players++;
            if(bot is not { IsValid:true }) { notBot++; continue; }
            if(!bot.IsBot) { notBot++; continue; }
            if(!bot.PawnIsAlive) { dead++; continue; }
            if(bot.TeamNum is not (2 or 3)) { teamBad++; continue; }
            if(bot.HasBeenControlledByPlayerThisRound) controlled++;
            var pawn=bot.PlayerPawn.Value;
            if(pawn is not { IsValid:true }) { pawnBad++; continue; }
            var money=bot.InGameMoneyServices;
            if(money==null) moneyNull++;
            bots++;
            bool isCT=bot.TeamNum==3;
            int budget=money!=null ? GetRoundSpendCap(isCT,money.Account < 2800) : 2000;
            var types=new[] { "smoke","flash","he","molotov" }.OrderBy(_ => Random.Shared.Next()).ToArray();
            int total=0;
            if(pawn.WeaponServices!=null)
            {
                foreach(var handle in pawn.WeaponServices.MyWeapons)
                {
                    var w=handle.Value;
                    if(w is not { IsValid:true } || w.As<CCSWeaponBase>().VData?.GearSlot != gear_slot_t.GEAR_SLOT_GRENADES) continue;
                    int ammo=w.As<CCSWeaponBase>().VData?.PrimaryAmmoType ?? -1;
                    total += ammo >= 0 && ammo < pawn.WeaponServices.Ammo.Length ? Math.Max(1,(int)pawn.WeaponServices.Ammo[ammo]) : 1;
                }
            }
            else weaponsNull++;
            foreach(var type in types)
            {
                if(total >= limit) break;
                eligible++;
                if(FindInventoryGrenade(bot,type) != null) { already++; continue; }
                int cost=(isCT ? CostCT : CostT)[type];
                int account=money?.Account ?? 3000;
                if(account < cost || budget < cost) { noMoney++; continue; }
                int before=money?.Account ?? 0;
                if(money!=null) money.Account-=cost;
                try
                {
                    if(bot.GiveNamedItem(InventoryItem(type,bot.TeamNum)!) == nint.Zero)
                        throw new InvalidOperationException("Grenade purchase failed");
                }
                catch(Exception error)
                {
                    if(money!=null) money.Account=before;
                    Logger.LogWarning("[NadeSystem] Purchase failed for {Bot} {Type}: {Error}", bot.PlayerName, type, error.Message);
                    continue;
                }
                budget-=cost;
                total++; bought++;
                if(money!=null) Utilities.SetStateChanged(bot,"CCSPlayerController","m_pInGameMoneyServices");
                Logger.LogInformation("[NadeSystem] Purchased {Type} for {Bot}, cost={Cost}, moneyServices={Money}", type, bot.PlayerName, cost, money != null);
            }
        }
        Logger.LogInformation("[NadeSystem] Buy pass mode={Mode} players={Players} notBot={NotBot} dead={Dead} teamBad={TeamBad} pawnBad={PawnBad} controlled={Controlled} weaponsNull={WeaponsNull} moneyNull={MoneyNull} bots={Bots} eligible={Eligible} already={Already} noMoney={NoMoney} bought={Bought}",
            _botNadesMode, players, notBot, dead, teamBad, pawnBad, controlled, weaponsNull, moneyNull, bots, eligible, already, noMoney, bought);
    }}
