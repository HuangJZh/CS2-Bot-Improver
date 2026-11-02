using BotLearning;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using Vec=System.Numerics.Vector3;
namespace NadeSystem;

public partial class NadeSystemPlugin
{
    private readonly RoundSpatialLearning _nadeLearning=new();
    private bool _nadeLearningEnabled=true,_nadeLearningExplore,_nadeLearningPriorityLogged;
    private void InitializeNadeLearning()
    {
        AddCommand("bot_nade_learning","Local host: status/reset/on/off",(caller,c)=>{
            if(caller!=null&&!(OperatingSystem.IsWindows()&&Environment.GetCommandLineArgs().Contains("-steam")
                &&!Environment.GetCommandLineArgs().Contains("-dedicated")&&caller is {IsValid:true,IsBot:false,Slot:0}))return;
            string mode=c.ArgCount>1?c.GetArg(1):"status";
            if(mode=="reset")_nadeLearning.Clear();
            if(mode=="on")_nadeLearningEnabled=true;
            if(mode=="off")_nadeLearningEnabled=false;
            Server.PrintToConsole($"[NadeLearning] enabled={_nadeLearningEnabled} rounds={_nadeLearning.LearnedRounds} regions={_nadeLearning.Regions}");
        });
    }
    private float LearnedUtilityScore(int team,GrenadeData g)=>_nadeLearningEnabled&&!_nadeLearningExplore&&team==3
        ?_nadeLearning.Risk(team,new(g.LandingPosition.X,g.LandingPosition.Y,g.LandingPosition.Z)):0;
    private IReadOnlyList<GrenadeData> LearnedUtilityOrder(int team,IReadOnlyList<GrenadeData> choices)
    {
        if(!_nadeLearningEnabled||_nadeLearningExplore||team!=3||!_nadeLearning.HasTeam(team))return choices;
        static bool Regular(GrenadeData g)=>g.GrenadeType is "smoke" or "flash" or "he" or "molotov";
        var regular=choices.Where(Regular).ToArray();
        var ordered=_nadeLearning.Prioritize(team,regular,g=>new Vec(g.LandingPosition.X,g.LandingPosition.Y,g.LandingPosition.Z),false);
        float highest=ordered.Count>0?LearnedUtilityScore(team,ordered[0]):0;
        if(highest>.05f&&!_nadeLearningPriorityLogged)
        {_nadeLearningPriorityLogged=true;Logger.LogInformation("Nade learning prioritizes previous defense region: score={Score}; normal inventory and eligibility still required",highest);}
        // Leave legacy decoy/special entries in their original slots.
        int index=0;return choices.Select(g=>Regular(g)?ordered[index++]:g).ToArray();
    }
    private float LearnedUtilityChance(int team,GrenadeData g,float baseline)
    {
        if(!_nadeLearningEnabled||_nadeLearningExplore)return baseline;
        float multiplier=_nadeLearning.UtilityMultiplier(team,new(g.LandingPosition.X,g.LandingPosition.Y,g.LandingPosition.Z));
        return Math.Clamp(baseline*multiplier,0,1);
    }
    private void RecordLearningDeath(EventPlayerDeath e)
    {
        if(!_nadeLearningEnabled||_roundOver||_freezeEndTime<=0||e.Weapon is "planted_c4" or "c4" or "worldspawn" or "trigger_hurt")return;
        if(e.Userid is not {IsValid:true} victim||e.Attacker is not {IsValid:true} attacker||victim.TeamNum==attacker.TeamNum
            ||victim.TeamNum is not (2 or 3)||attacker.TeamNum is not (2 or 3)||victim.PlayerPawn.Value?.AbsOrigin is not {} p)return;
        var rules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if(rules!=null&&!rules.WarmupPeriod&&!rules.FreezePeriod)_nadeLearning.Death(victim.TeamNum,new Vec(p.X,p.Y,p.Z));
    }
    private void CompleteNadeLearning(int winner)
    {
        if(_nadeLearning.Complete(winner))Logger.LogInformation("Nade learning updated: rounds={Rounds}, regions={Regions}",_nadeLearning.LearnedRounds,_nadeLearning.Regions);
    }
}
