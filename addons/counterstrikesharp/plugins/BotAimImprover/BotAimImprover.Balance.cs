using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private readonly EngagementAimIntents _aimIntents=new();
    private float _humanTeamsScanAt;
    private int _humanTeams;
    private HumanAimProfile? _baseCombatProfile,_allyCombatProfile;
    private string? _balanceDifficulty;
    private float _balanceStrength;
    private HumanAimProfile CombatProfile(CCSPlayerController controller,float now)
    {
        if(_baseCombatProfile==null||_balanceDifficulty!=_humanDifficulty||_balanceStrength!=_humanSettings.HumanAllyStrength)
        {
            _balanceDifficulty=_humanDifficulty;_balanceStrength=_humanSettings.HumanAllyStrength;
            _baseCombatProfile=HumanAimProfile.For(_humanDifficulty);
            _allyCombatProfile=CombatBalancePolicy.AllyProfile(_baseCombatProfile,_balanceStrength);
        }
        if(now>=_humanTeamsScanAt)
        {
            _humanTeamsScanAt=now+.5f;_humanTeams=0;
            // Keep the handicap when the human dies, and follow their team after a side switch.
            foreach(var p in EntityPlayers())
                if(p.IsValid&&!p.IsBot&&p.TeamNum is 2 or 3)_humanTeams|=1<<p.TeamNum;
        }
        return CombatBalancePolicy.IsHumanAlly(controller.TeamNum,_humanTeams)?_allyCombatProfile!:_baseCombatProfile;
    }
    private int[] AimOrder(CCSPlayerController controller,CCSPlayerPawn pawn,CCSPlayerPawn target)
    {
        var item=pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName;
        if(_aimMode==AimMode.HEAD)return item=="weapon_awp"?_priorityTrunk:_priorityHead;
        if(_aimMode==AimMode.BODY)return _priorityBody;
        float chance=CombatBalancePolicy.HeadIntentChance(_humanDifficulty,HumanFireContext.Classify(item),item);
        bool head=_aimIntents.Resolve(controller.Slot,new(pawn.EntityHandle.Raw,target.EntityHandle.Raw,
            pawn.WeaponServices?.ActiveWeapon.Raw??uint.MaxValue,(int)_aimMode),chance,Server.CurrentTime,Random.Shared.NextSingle);
        return head?_priorityHead:item is "weapon_awp" or "weapon_scar20" or "weapon_g3sg1"?_priorityTrunk:_priorityUpperChest;
    }
}
