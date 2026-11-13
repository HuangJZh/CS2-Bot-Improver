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
        return bot.PlayerPawn.Value?.WeaponServices?.MyWeapons
            .Select(w => w.Value).FirstOrDefault(w => w is { IsValid: true } && UtilityPurchasePolicy.InventoryMatches(type,w.DesignerName));
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

    private static bool HasPrimaryWeapon(CCSPlayerController player) =>
        player.PlayerPawn.Value?.WeaponServices?.MyWeapons.Any(h =>
            h.Value is { IsValid:true } w && w.As<CCSWeaponBase>().VData?.GearSlot == gear_slot_t.GEAR_SLOT_RIFLE) == true;

    // BotBuy finishes its own purchases and automatic donations in the first
    // three seconds. BotSupport processes early wheel requests at four seconds.
    // Every entry point (including FreezeEnd and retries) must respect this gate.
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
        if(Server.CurrentTime - _roundStartTime < UtilityPurchasePolicy.EarliestPurchaseSeconds) return;
        var controllers=Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").ToArray();
        // Keep donation funds until a living human in spawn has bought/received
        // a primary or left the buy zone. Do not consume those funds on utility.
        var waitingTeams=controllers.Where(p => p is { IsValid:true, IsBot:false, PawnIsAlive:true }
            && p.TeamNum is 2 or 3 && p.PlayerPawn.Value is { IsValid:true, InBuyZone:true }
            && !HasPrimaryWeapon(p)).Select(p => p.TeamNum).ToHashSet();
        int limit=Math.Clamp(ConVar.Find("ammo_grenade_limit_total")?.GetPrimitiveValue<int>() ?? 4,0,4);
        int players=0,notBot=0,dead=0,teamBad=0,pawnBad=0,controlled=0,weaponsNull=0,moneyNull=0;
        int bots=0,eligible=0,bought=0,noMoney=0,already=0,waitingWeapon=0,waitingDonation=0;
        // GetPlayers filters by Connected and Server.MaxPlayers. BotHider/local
        // servers can expose live bot entities outside that client roster. Use
        // the same entity enumeration as the grenade throwing logic instead.
        foreach(var bot in controllers)
        {
            players++;
            if(bot is not { IsValid:true }) { notBot++; continue; }
            if(!bot.IsBot) { notBot++; continue; }
            if(!bot.PawnIsAlive) { dead++; continue; }
            if(bot.TeamNum is not (2 or 3)) { teamBad++; continue; }
            if(bot.HasBeenControlledByPlayerThisRound) { controlled++; continue; }
            var pawn=bot.PlayerPawn.Value;
            if(pawn is not { IsValid:true }) { pawnBad++; continue; }
            var money=bot.InGameMoneyServices;
            if(money==null) { moneyNull++; continue; }
            var services=pawn.WeaponServices;
            if(services==null) { weaponsNull++; continue; }
            bots++;
            // A genuine pistol round may spend its remaining pistol/armor budget
            // on grenades. Outside that case, do not spend a gunless bot's cash.
            bool teammateNeedsGun=waitingTeams.Contains(bot.TeamNum);
            if(!UtilityPurchasePolicy.CanSpend(HasPrimaryWeapon(bot),IsPistolRound(),money.Account,teammateNeedsGun))
            {
                if(teammateNeedsGun) waitingDonation++; else waitingWeapon++;
                continue;
            }
            bool isCT=bot.TeamNum==3;
            int budget=GetRoundSpendCap(isCT,money.Account < 2800);
            var types=new[] { "smoke","flash","he","molotov" }.OrderBy(_ => Random.Shared.Next()).ToArray();
            int total=0;
            foreach(var handle in services.MyWeapons)
            {
                var w=handle.Value;
                if(w is not { IsValid:true } || w.As<CCSWeaponBase>().VData?.GearSlot != gear_slot_t.GEAR_SLOT_GRENADES) continue;
                int ammo=w.As<CCSWeaponBase>().VData?.PrimaryAmmoType ?? -1;
                total += ammo >= 0 && ammo < services.Ammo.Length ? Math.Max(1,(int)services.Ammo[ammo]) : 1;
            }
            foreach(var type in types)
            {
                if(total >= limit) break;
                eligible++;
                if(FindInventoryGrenade(bot,type) != null) { already++; continue; }
                int cost=(isCT ? CostCT : CostT)[type];
                int account=money.Account;
                if(account < cost || budget < cost) { noMoney++; continue; }
                int before=money.Account;
                money.Account-=cost;
                try
                {
                    var created=bot.GiveNamedItem(InventoryItem(type,bot.TeamNum)!);
                    if(created == nint.Zero)
                        throw new InvalidOperationException("Grenade purchase failed");
                    // GiveNamedItem can create a world item when acquisition is
                    // rejected. Charge only for an item actually added to inventory.
                    if(!services.MyWeapons.Any(h=>h.Value is {IsValid:true} w&&w.Handle==created))
                    {
                        var rejected=new CBasePlayerWeapon(created);
                        if(rejected.IsValid)rejected.Remove();
                        throw new InvalidOperationException("Grenade was not acquired; removed new world item");
                    }
                }
                catch(Exception error)
                {
                    money.Account=before;
                    Logger.LogWarning("[NadeSystem] Purchase failed for {Bot} {Type}: {Error}", bot.PlayerName, type, error.Message);
                    continue;
                }
                budget-=cost;
                total++; bought++;
                Utilities.SetStateChanged(bot,"CCSPlayerController","m_pInGameMoneyServices");
                Logger.LogInformation("[NadeSystem] Purchased {Type} for {Bot}, cost={Cost}", type, bot.PlayerName, cost);
            }
        }
        Logger.LogInformation("[NadeSystem] Buy pass mode={Mode} players={Players} notBot={NotBot} dead={Dead} teamBad={TeamBad} pawnBad={PawnBad} controlled={Controlled} weaponsNull={WeaponsNull} moneyNull={MoneyNull} bots={Bots} waitingWeapon={WaitingWeapon} waitingDonation={WaitingDonation} eligible={Eligible} already={Already} noMoney={NoMoney} bought={Bought}",
            _botNadesMode, players, notBot, dead, teamBad, pawnBad, controlled, weaponsNull, moneyNull, bots, waitingWeapon, waitingDonation, eligible, already, noMoney, bought);
    }}
