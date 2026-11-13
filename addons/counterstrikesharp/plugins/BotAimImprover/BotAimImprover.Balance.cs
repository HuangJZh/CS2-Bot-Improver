using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private readonly EngagementAimIntents _aimIntents=new();
    private float _humanTeamsScanAt;
    private int _humanTeams;
    private HumanAimProfile? _baseCombatProfile,_allyCombatProfile,_enemyCombatProfile;
    private string? _balanceDifficulty;
    private float _balanceStrength,_enemyStrength;
    private readonly EnemyEliteRoster _enemyElites=new();
    private readonly Dictionary<float,HumanAimProfile> _eliteProfiles=new();
    private HumanAimProfile CombatProfile(CCSPlayerController controller,float now)
    {
        if(_baseCombatProfile==null||_balanceDifficulty!=_humanDifficulty||_balanceStrength!=_humanSettings.HumanAllyStrength
            ||_enemyStrength!=_humanSettings.EnemyStrength)
        {
            _balanceDifficulty=_humanDifficulty;_balanceStrength=_humanSettings.HumanAllyStrength;
            _baseCombatProfile=HumanAimProfile.For(_humanDifficulty);
            _enemyStrength=_humanSettings.EnemyStrength;
            _enemyCombatProfile=CombatBalancePolicy.EnemyProfile(_baseCombatProfile,_enemyStrength,_humanDifficulty);
            // The handicap is relative to today's ordinary enemies, including
            // explicit per-difficulty reaction targets, not the older baseline.
            _allyCombatProfile=CombatBalancePolicy.AllyProfile(_enemyCombatProfile,_balanceStrength);
            _eliteProfiles.Clear();
        }
        if(now>=_humanTeamsScanAt)
        {
            _humanTeamsScanAt=now+.5f;_humanTeams=0;
            // Keep the handicap when the human dies, and follow their team after a side switch.
            var players=EntityPlayers();
            foreach(var p in players)
                if(p.IsValid&&!p.IsBot&&p.TeamNum is 2 or 3)_humanTeams|=1<<p.TeamNum;
            if(_humanSettings.EnemyElites)
            {
                if(_enemyElites.Refresh(_humanTeams,players.Where(p=>p.IsValid&&p.IsBot&&!p.HasBeenControlledByPlayerThisRound)
                    .Select(p=>new BalanceBot(p.EntityHandle.Raw,p.TeamNum)),Random.Shared))
                {
                    var selected=players.Where(p=>_enemyElites.Strengths.ContainsKey(p.EntityHandle.Raw))
                        .Select(p=>$"{p.PlayerName}(slot={p.Slot},x{_enemyElites.Strength(p.EntityHandle.Raw):F3})");
                    Logger.LogInformation("Enemy elites selected: humanTeams={Teams}, {Elites}",_humanTeams,string.Join(", ",selected));
                }
            }
            else _enemyElites.Reset();
        }
        if(CombatBalancePolicy.IsHumanAlly(controller.TeamNum,_humanTeams))return _allyCombatProfile!;
        if(!CombatBalancePolicy.IsHumanEnemy(controller.TeamNum,_humanTeams))return _baseCombatProfile!;
        float strength=_humanSettings.EnemyElites?_enemyElites.Strength(controller.EntityHandle.Raw):1;
        if(strength<=1)return _enemyCombatProfile!;
        if(!_eliteProfiles.TryGetValue(strength,out var elite))
            _eliteProfiles[strength]=elite=CombatBalancePolicy.AllyProfile(_enemyCombatProfile!,strength);
        return elite;
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
