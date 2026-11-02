using BotLearning;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private readonly RoundSpatialLearning _routeLearning=new();
    private void InitializeRoundLearning()
    {
        RegisterEventHandler<EventPlayerDeath>((e,i)=>{
            if(_humanSettings.RoundLearning&&e.Weapon is not ("planted_c4" or "c4" or "worldspawn" or "trigger_hurt")
                &&e.Userid is {IsValid:true} victim&&e.Attacker is {IsValid:true} attacker&&attacker.TeamNum!=victim.TeamNum
                &&victim.TeamNum is 2 or 3&&attacker.TeamNum is 2 or 3&&victim.PlayerPawn.Value?.AbsOrigin is {} p)
            {
                var rules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
                if(rules!=null&&!rules.WarmupPeriod&&!rules.FreezePeriod)_routeLearning.Death(victim.TeamNum,new(p.X,p.Y,p.Z));
            }
            return HookResult.Continue;
        });
        RegisterEventHandler<EventRoundEnd>((e,i)=>{
            if(_routeLearning.Complete(e.Winner))Logger.LogInformation("Round learning updated: rounds={Rounds}, regions={Regions}",_routeLearning.LearnedRounds,_routeLearning.Regions);
            return HookResult.Continue;
        });
        AddCommand("bot_route_learning","Local host: status/reset/on/off",(caller,c)=>{
            if(!IsLocalAimConsole(caller))return;
            string mode=c.ArgCount>1?c.GetArg(1):"status";
            if(mode=="reset")_routeLearning.Clear();
            if(mode=="on")_humanSettings.RoundLearning=true;
            if(mode=="off")_humanSettings.RoundLearning=false;
            Server.PrintToConsole($"[RouteLearning] enabled={_humanSettings.RoundLearning} rounds={_routeLearning.LearnedRounds} regions={_routeLearning.Regions}");
        });
    }
}
