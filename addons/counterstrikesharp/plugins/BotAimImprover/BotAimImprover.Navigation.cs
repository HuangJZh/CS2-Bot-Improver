using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;
using Vec=System.Numerics.Vector3;
namespace BotAimImprover;

public partial class BotAimImprover
{
    // Native MoveTo binding adapted from ed0ard/CS2-Bot-Nav, AGPL-3.0,
    // commit 0d1d04f8eb748e3434462d2a799ca89ab299e583. See docs/NAVIGATION-SOURCES.md.
    private MemoryFunctionVoid<nint,nint,int>? _navigationMoveTo;
    private TeamNavigationGraph? _navigationGraph;
    private string? _navigationMap;
    private float _navigationTick;
    private bool _navigationPrepared,_navigationBombStage,_navigationFailed,_navigationRoundEnded;
    private readonly Vector _navigationTarget=new(0,0,0);
    private sealed class NavigationPlan
    {
        public required uint Pawn;
        public required List<int> Path;
        public required Vec Goal;
        public required int Team;
        public float Started,LastProgress,BestDistance=float.PositiveInfinity,SpacingSince=-1,LogAt,SpacingUntil,SpacingCooldown;
        public long SpacingToken;
        public int Cursor;
    }
    private readonly Dictionary<int,NavigationPlan> _navigationPlans=new();
    private readonly Dictionary<string,int> _navigationCounters=new();
    private void NavCount(string reason)=>_navigationCounters[reason]=_navigationCounters.GetValueOrDefault(reason)+1;
    private void InitializeTeamNavigation()
    {
        try
        {
            string signature=OperatingSystem.IsWindows()
                ? "F2 0F 10 02 F2 0F 11 81 E8 02 00 00 8B 42 08 48 8D 91 E0 02 00 00 89 81 F0 02 00 00 44 89 81 F4 02 00 00 E9 ? ? ? ?"
                : "48 8B 06 48 89 87 E0 02 00 00 8B 46 08 48 8D B7 D8 02 00 00 89 97 EC 02 00 00 89 87 E8 02 00 00 E9 ? ? ? ?";
            _navigationMoveTo=new(signature);
            if(_navigationMoveTo.Handle==nint.Zero)throw new InvalidOperationException("MoveTo not found");
        }
        catch(Exception e){_navigationFailed=true;Logger.LogWarning(e,"Team navigation unavailable; keeping native routes");}
        RegisterEventHandler<EventRoundEnd>((e,i)=>{ResetTeamNavigation();_navigationRoundEnded=true;return HookResult.Continue;});
        AddCommand("bot_team_nav","Local host: on/off/status",(caller,c)=>{
            if(!IsLocalAimConsole(caller))return;
            string mode=c.ArgCount>1?c.GetArg(1):"status";
            if(mode=="off"){_humanSettings.TeamNavigation=false;ResetTeamNavigation();}
            if(mode=="on"){_humanSettings.TeamNavigation=true;ResetTeamNavigation();}
            Server.PrintToConsole($"[TeamNav] enabled={_humanSettings.TeamNavigation} failed={_navigationFailed} map={_navigationMap} areas={_navigationGraph?.Areas.Count??0} plans={_navigationPlans.Count} counters={JsonSerializer.Serialize(_navigationCounters)}");
        });
    }
    private void ResetTeamNavigation()
    {
        // No persistent movement or button overrides are created by this module.
        foreach(int slot in _navigationPlans.Keys.ToArray())EndNavigation(slot,"reset");
        _navigationPrepared=false;_navigationBombStage=false;_navigationTick=0;_navigationRoundEnded=false;
    }
    private bool LoadTeamNavigation()
    {
        if(_navigationMap==Server.MapName)return _navigationGraph!=null;
        _navigationMap=Server.MapName;_navigationGraph=null;
        foreach(int slot in _navigationPlans.Keys.ToArray())EndNavigation(slot,"map-changed");
        var path=Path.Combine(ModuleDirectory,"navigation",Server.MapName+".json");
        if(!File.Exists(path)){NavCount("unsupported-map");return false;}
        var data=JsonSerializer.Deserialize<NavigationMap>(File.ReadAllText(path));
        var mapFile=Path.GetFullPath(Path.Combine(ModuleDirectory,"../../../../maps",Server.MapName+".vpk"));
        if(data==null||data.Map!=Server.MapName||!File.Exists(mapFile))return false;
        using var stream=File.OpenRead(mapFile);
        if(!Convert.ToHexString(SHA256.HashData(stream)).Equals(data.SourceMapSha256,StringComparison.OrdinalIgnoreCase))
        {NavCount("map-checksum-mismatch");Logger.LogWarning("Team navigation graph is outdated for {Map}; using native routes",Server.MapName);return false;}
        _navigationGraph=new(data.Areas);NavCount("map-loaded");
        Logger.LogInformation("Team navigation ready for {Map}: {Areas} native nav areas",Server.MapName,data.Areas.Count);
        return true;
    }
    private List<Vec> NavigationSites()
    {
        var sites=new List<Vec>();
        foreach(var entity in Utilities.FindAllEntitiesByDesignerName<CBaseEntity>("func_bomb_target"))
        {
            if(!entity.IsValid||entity.AbsOrigin is not {} p||entity.Collision is not {} c)continue;
            var center=new Vec(p.X+(c.Mins.X+c.Maxs.X)*.5f,p.Y+(c.Mins.Y+c.Maxs.Y)*.5f,p.Z+c.Mins.Z+8);
            if(_navigationGraph!.Locate(center,700) is {} id)sites.Add(_navigationGraph.Areas[id].Point);
        }
        return sites.Distinct().OrderBy(p=>p.X).ThenBy(p=>p.Y).ToList();
    }
    private void PrepareNavigation(float now)
    {
        _navigationPrepared=true;
        var sites=_bombPlanted?new List<Vec>{_bombPosition}:NavigationSites();
        if(sites.Count==0){NavCount("no-reachable-site");return;}
        var all=EntityPlayers().Where(p=>p.IsValid&&p.PawnIsAlive&&p.TeamNum is 2 or 3).ToArray();
        foreach(int team in new[]{2,3})
        {
            if(_bombPlanted&&team==2)continue; // Keep native post-plant decisions.
            var members=all.Where(p=>p.IsBot&&p.TeamNum==team&&!p.HasBeenControlledByPlayerThisRound&&!_supportPlans.ContainsKey(p.Slot)).OrderBy(p=>p.Slot).ToList();
            // Carrier goes first, so the rest can choose supporting alternate routes.
            members=members.OrderByDescending(p=>p.PlayerPawn.Value?.WeaponServices?.MyWeapons.Any(w=>w.Value?.DesignerName=="weapon_c4")==true).ToList();
            var occupancy=new Dictionary<int,int>();var siteCounts=new int[sites.Count];
            foreach(var human in all.Where(p=>!p.IsBot&&p.TeamNum==team))
                if(human.PlayerPawn.Value is {IsValid:true} hp&&hp.AbsOrigin!=null)
                    siteCounts[Enumerable.Range(0,sites.Count).MinBy(i=>Vec.DistanceSquared(Position(hp),sites[i]))]++;
            int attackSite=Random.Shared.Next(sites.Count);
            bool explore=Random.Shared.NextSingle()<.20f;
            Dictionary<int,float>? learnedRisk=null;
            if(team==2&&_humanSettings.RoundLearning&&!explore&&_routeLearning.HasTeam(team))
                learnedRisk=_navigationGraph!.Areas.ToDictionary(a=>a.Key,a=>_routeLearning.Risk(team,a.Value.Point));
            foreach(var p in members)
            {
                if(p.PlayerPawn.Value is not {IsValid:true} pawn||pawn.AbsOrigin==null)continue;
                var origin=Position(pawn);
                int site=TeamNavigationPolicy.ChooseSite(team,attackSite,origin,sites,siteCounts);
                if(_navigationGraph!.Locate(origin) is not {} start||_navigationGraph.Locate(sites[site],700) is not {} goal)continue;
                var path=_navigationGraph.DiversifiedPath(start,goal,occupancy,learnedRisk);
                if(path==null||path.Count<2){NavCount("unreachable-or-arrived");continue;}
                siteCounts[site]++;
                foreach(int id in path)occupancy[id]=occupancy.GetValueOrDefault(id)+1;
                _navigationPlans[p.Slot]=new(){Pawn=p.PlayerPawn.Raw,Path=path,Goal=sites[site],Team=team,Started=now,LastProgress=now};
                NavCount("assigned");
                WriteTelemetry(new{kind="navigation_plan",time=now,slot=p.Slot,team,stage=_bombPlanted?"retake":"opening",
                    areas=path.Count,path=path.ToArray(),length=_navigationGraph.Length(path),goal_x=sites[site].X,goal_y=sites[site].Y,
                    learning_rounds=_routeLearning.LearnedRounds,learning_explore=explore,
                    learned_path_risk=path.Average(id=>learnedRisk?.GetValueOrDefault(id)??0)});
            }
        }
    }
    private void EndNavigation(int slot,string reason)
    {
        if(!_navigationPlans.Remove(slot,out var plan))return;
        CancelNavigationSpacing(slot,plan);
        NavCount(reason);WriteTelemetry(new{kind="navigation_end",time=Server.CurrentTime,slot,reason});
    }
    private void CancelNavigationSpacing(int slot,NavigationPlan plan)
    {
        if(plan.SpacingToken>0)try{_humanController?.CancelUsercmdInjection(slot,plan.SpacingToken);}catch(Exception){}
        plan.SpacingToken=0;
    }
    private void TickTeamNavigation()
    {
        if(!_humanSettings.TeamNavigation||!HumanActive||_navigationFailed||_navigationRoundEnded||_navigationMoveTo==null)return;
        float now=Server.CurrentTime;if(now<_navigationTick)return;_navigationTick=now+.2f;
        try
        {
            ReadObjective(now);if(!_roundLive)return;
            if(!LoadTeamNavigation())return;
            if(_bombPlanted!=_navigationBombStage)
            {foreach(int slot in _navigationPlans.Keys.ToArray())EndNavigation(slot,"objective-stage-changed");_navigationPrepared=false;_navigationBombStage=_bombPlanted;}
            if(!_navigationPrepared)
            {
                if(!_bombPlanted&&now-_roundStartedAt>35){_navigationPrepared=true;return;}
                PrepareNavigation(now);
            }
            foreach(var (slot,plan) in _navigationPlans.ToArray())
            {
                var p=EntityPlayerAtSlot(slot);
                if(p is not {IsValid:true,IsBot:true,PawnIsAlive:true}||p.PlayerPawn.Raw!=plan.Pawn||p.TeamNum!=plan.Team
                    ||p.HasBeenControlledByPlayerThisRound||p.PlayerPawn.Value is not {IsValid:true} pawn||pawn.Bot is not {} bot||pawn.AbsOrigin==null)
                {EndNavigation(slot,"ownership-ended");continue;}
                var origin=Position(pawn);float distance=Vec.Distance(origin,plan.Goal);
                if(_humanSettings.RoundLearning)_routeLearning.Transit(p.TeamNum,origin);
                if(distance<(_bombPlanted?650:420)){EndNavigation(slot,"native-objective-handoff");continue;}
                if(now-plan.Started>(_bombPlanted?20:35)){EndNavigation(slot,"lease-expired");continue;}
                bool combat=bot.IsEnemyVisible||bot.IsAttacking||(_humanStates.TryGetValue(slot,out var state)&&now<state.UnderFireUntil);
                var item=pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName??"";
                bool objectiveAction=pawn.IsDefusing||(_plantingUntil.TryGetValue(slot,out var planting)&&now<planting)||item=="weapon_c4";
                bool busy=objectiveAction||item.Contains("grenade")||item is "weapon_flashbang" or "weapon_molotov" or "weapon_incgrenade";
                bool urgent=(_bombPlanted&&_bombDeadline-now<distance/220+16)||(!_bombPlanted&&_roundDeadline-now<25);
                if(urgent||objectiveAction){EndNavigation(slot,"urgent-objective");continue;}
                bool nativeOwner=OtherViewOwner(slot)||_coverPeeks.ContainsKey(slot)||!pawn.BotAllowActive;
                if(!TeamNavigationPolicy.CanDrive(new(combat,busy,(pawn.Flags&1)!=0&&pawn.MoveType!=MoveType_t.MOVETYPE_LADDER,
                    bot.IsFriendInTheWay||bot.IsWaitingBehindFriend,_roundLive,nativeOwner,urgent,now-plan.Started,35)))
                {CancelNavigationSpacing(slot,plan);plan.LastProgress=now;NavCount("yield-to-native");continue;}
                var nodes=plan.Path;
                // Advance only on genuine approach to the next area, not on time.
                int oldCursor=plan.Cursor;
                if(_navigationGraph!.Locate(origin,100) is {} standing)
                    for(int i=plan.Cursor;i<Math.Min(nodes.Count,plan.Cursor+8);i++)if(nodes[i]==standing)plan.Cursor=i;
                while(plan.Cursor<nodes.Count-1&&Vec.Distance(origin,_navigationGraph.Areas[nodes[plan.Cursor]].Point)<125)plan.Cursor++;
                if(oldCursor!=plan.Cursor){plan.BestDistance=float.PositiveInfinity;plan.LastProgress=now;}
                float waypointDistance=Vec.Distance(origin,_navigationGraph!.Areas[nodes[plan.Cursor]].Point);
                if(waypointDistance<plan.BestDistance-20){plan.BestDistance=waypointDistance;plan.LastProgress=now;}
                if(now-plan.LastProgress>3){EndNavigation(slot,"no-progress-native-recovery");continue;}
                int look=plan.Cursor;float length=0;
                while(look<nodes.Count-1&&length<220)
                {length+=Vec.Distance(_navigationGraph.Areas[nodes[look]].Point,_navigationGraph.Areas[nodes[look+1]].Point);look++;}
                var destination=_navigationGraph.Areas[nodes[look]].Point;
                bool crowd=now-plan.Started>3&&EntityPlayers().Any(m=>m.Slot!=slot&&m.TeamNum==p.TeamNum&&m.PawnIsAlive
                    &&m.PlayerPawn.Value is {IsValid:true} mp&&mp.AbsOrigin!=null&&TeamNavigationPolicy.TooCloseAhead(origin,destination,Position(mp)));
                ResetSilentMovement(slot); // Do not randomly slow a coordinated transit.
                if(crowd)
                {
                    if(plan.SpacingSince<0)plan.SpacingSince=now;
                    if(now-plan.SpacingSince>.6f){plan.SpacingCooldown=now+1;plan.SpacingSince=-1;CancelNavigationSpacing(slot,plan);}
                    // Walk briefly instead of holding the native goal at the current
                    // origin. Never freeze an entire column behind its own followers.
                    if(now>=plan.SpacingCooldown&&now>=plan.SpacingUntil)
                    {plan.SpacingToken=_humanController?.InjectUsercmd(slot,(ulong)PlayerButtons.Walk,220)??0;plan.SpacingUntil=now+.22f;NavCount("spacing-yield");}
                }
                else{plan.SpacingSince=-1;CancelNavigationSpacing(slot,plan);}
                _navigationTarget.X=destination.X;_navigationTarget.Y=destination.Y;_navigationTarget.Z=destination.Z;
                _navigationMoveTo.Invoke(bot.Handle,_navigationTarget.Handle,1);NavCount("move-to");
                if(now>=plan.LogAt)
                {
                    plan.LogAt=now+1;
                    WriteTelemetry(new{kind="navigation_step",time=now,slot,cursor=plan.Cursor,native_path_index=bot.PathIndex,
                        x=origin.X,y=origin.Y,z=origin.Z,target_x=destination.X,target_y=destination.Y,target_z=destination.Z,
                        goal_distance=distance,spacing=crowd});
                }
            }
        }
        catch(Exception e)
        {_navigationFailed=true;foreach(int slot in _navigationPlans.Keys.ToArray())EndNavigation(slot,"runtime-fallback");Logger.LogError(e,"Team navigation disabled after runtime failure; native navigation retained");}
    }
}
