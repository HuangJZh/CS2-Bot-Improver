using BotPurchasePolicy;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;

namespace BotBuyPatch;
public sealed partial class BotBuyPatch
{
    private HashSet<uint> _sniperBuyers=new();
    private bool _sniperPolicyActive;
    private void ConfigureSniperPurchases(IReadOnlyList<CCSPlayerController> players)
    {
        var live=players.Where(p=>p.IsValid&&p.PawnIsAlive&&p.TeamNum is 2 or 3).ToArray();
        _sniperBuyers=TeamSniperPolicy.Assign(live.Select(p=>
        {
            var weapons=WeaponNames(p).ToArray();
            return new SniperBuyer(p.EntityHandle.Raw,p.Slot,p.TeamNum,p.InGameMoneyServices?.Account??0,
                p.IsBot&&!p.ControllingBot&&!p.HasBeenControlledByPlayerThisRound,
                PurchasePolicy.HasPrimary(weapons),weapons.Any(TeamSniperPolicy.IsSniper));
        }).ToArray());
        _sniperPolicyActive=true;
        foreach(var p in live)
        {
            if(!p.IsBot||p.ControllingBot||p.HasBeenControlledByPlayerThisRound||HasPrimaryWeapon(p))continue;
            int money=p.InGameMoneyServices?.Account??0;
            // Below Scout price the original pistol/eco buying cannot add a
            // sniper. Preserve that behavior instead of replacing pistol rounds.
            if(money<1700)continue;
            bool sniper=_sniperBuyers.Contains(p.EntityHandle.Raw);
            string? primary=sniper?"awp":TeamSniperPolicy.RifleAlias(p.TeamNum,money,Random.Shared.Next(2)==0);
            if(primary==null)continue;
            // BotHider/custom loadouts do not reliably honor native buy aliases
            // for primaries. Stop native buying, then use this plugin's existing
            // inventory-aware, paid Buy path. Never issue two primary commands.
            if(_buyController?.SetBuyPlan(p.Slot,"")==true)
            {
                _carriedBuyPlans.Add(p.Slot);
                Logger.LogInformation("Team primary plan team={Team} slot={Slot} gun={Gun} sniper={Sniper}",p.TeamNum,p.Slot,primary,sniper);
                uint controller=p.EntityHandle.Raw,pawn=p.PlayerPawn.Raw;
                RoundTimer(.25f,()=>
                {
                    if(!p.IsValid||p.EntityHandle.Raw!=controller||p.PlayerPawn.Raw!=pawn||!p.PawnIsAlive
                        ||p.PlayerPawn.Value is not {IsValid:true,InBuyZone:true} livePawn)return;
                    var rules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
                    if(rules==null||rules.BuyTimeEnded)return;
                    if(!Buy(p,"weapon_"+primary,plannedPrimary:true)&&!HasPrimaryWeapon(p))
                    {
                        var fallback=TeamSniperPolicy.RifleAlias(p.TeamNum,p.InGameMoneyServices?.Account??0);
                        if(fallback!=null)Buy(p,"weapon_"+fallback,plannedPrimary:true);
                    }
                    if(livePawn.ArmorValue<=40)
                    {
                        if(!Buy(p,"item_assaultsuit"))Buy(p,"item_kevlar");
                    }
                    else if(livePawn.ArmorValue>=100&&livePawn.ItemServices?.As<CCSPlayer_ItemServices>().HasHelmet==false)
                        Buy(p,"item_assaultsuit");
                    if(p.TeamNum==3&&livePawn.ItemServices?.As<CCSPlayer_ItemServices>().HasDefuser==false)
                        Buy(p,"item_defuser");
                });
            }
            else Logger.LogError("Could not install team primary plan slot={Slot}",p.Slot);
        }
    }
    private static void AddNeededEquipment(CCSPlayerController player,List<string> plan)
    {
        var pawn=player.PlayerPawn.Value!;
        var equipment=pawn.ItemServices?.As<CCSPlayer_ItemServices>();
        if(pawn.ArmorValue<=40){plan.Add("vesthelm");plan.Add("vest");}
        else if(pawn.ArmorValue>=100&&equipment?.HasHelmet==false)plan.Add("vesthelm");
        if(player.TeamNum==3&&equipment?.HasDefuser==false)plan.Add("defuser");
    }
    private bool CanPurchaseSniper(CCSPlayerController buyer)
    {
        if(!_sniperPolicyActive||!_sniperBuyers.Contains(buyer.EntityHandle.Raw))return false;
        int held=Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Count(p=>p.IsValid&&p.PawnIsAlive&&p.TeamNum==buyer.TeamNum&&WeaponNames(p).Any(TeamSniperPolicy.IsSniper));
        return held<TeamSniperPolicy.Limit(buyer.TeamNum);
    }
}
