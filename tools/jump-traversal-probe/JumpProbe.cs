using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using System.Text.Json;

public sealed class JumpCase
{
    public string Map{get;set;}="";
    public string Name{get;set;}="";
    public int From{get;set;}
    public int To{get;set;}
    public float[] Start{get;set;}=[];
    public float[] End{get;set;}=[];
    public float[][] EndCorners{get;set;}=[];
    public string SourceMapSha256{get;set;}="";
}
public sealed class JumpProbe:BasePlugin
{
    public override string ModuleName=>"Owned loopback jump audit";
    public override string ModuleVersion=>"1.0";
    private string Root="";
    private string Run="";
    private readonly string[] _maps=["de_dust2","de_mirage","de_inferno"];
    private List<JumpCase> _cases=[];
    private List<JumpCase> _selected=[];
    private MemoryFunctionVoid<nint,nint,int>? _move;
    private float _readyAt=float.PositiveInfinity,_trialAt,_nextSample,_lastMove,_lastX,_lastY,_distance,_groundSince=-1;
    private int _index,_variant,_slot=-1,_jumps;
    private uint _pawn;
    private nint _botPointer;
    private bool _setup,_trial,_done,_lastGround;
    private float _liveAt=float.PositiveInfinity;
    private float _startZ,_goalZ,_maxZ;
    private Vector _goal=new();
    private void Log(object value)=>File.AppendAllText(Root+Run+".jsonl",JsonSerializer.Serialize(value)+"\n");
    public override void Load(bool hotReload)
    {
        var args=Environment.GetCommandLineArgs();int ip=Array.IndexOf(args,"-ip"),port=Array.IndexOf(args,"-port");
        if(!OperatingSystem.IsWindows()||!args.Contains("-jump-audit")||!args.Contains("-insecure")||!args.Contains("-dedicated")||ip<0||port<0
            ||ip+1>=args.Length||port+1>=args.Length||args[ip+1]!="127.0.0.1"||args[port+1]!="27025")throw new Exception("Owned Windows loopback fixture only");
        string Option(string key){int n=Array.IndexOf(args,key);if(n<0||n+1>=args.Length)throw new Exception("Missing "+key);return args[n+1];}
        Root=Path.GetFullPath(Option("-jump-audit-root"))+Path.DirectorySeparatorChar;Run=Option("-jump-audit-run");
        if(!System.Text.RegularExpressions.Regex.IsMatch(Run,"^[a-zA-Z0-9_-]+$")||File.Exists(Root+Run+".jsonl"))throw new Exception("Use a fresh audit run name");
        _cases=JsonSerializer.Deserialize<List<JumpCase>>(File.ReadAllText(Root+"cases.json"))!;
        _move=new("F2 0F 10 02 F2 0F 11 81 E8 02 00 00 8B 42 08 48 8D 91 E0 02 00 00 89 81 F0 02 00 00 44 89 81 F4 02 00 00 E9 ? ? ? ?");
        if(_move.Handle==0)throw new Exception("MoveTo missing");
        _move.Hook(HoldTrialDestination,HookMode.Pre);
        Log(new{kind="load",properties=typeof(CCSBot).GetProperties().Where(p=>p.Name.Contains("Path")||p.Name.Contains("Jump")||p.Name.Contains("Goal")).Select(p=>p.Name).ToArray()});
        RegisterEventHandler<EventRoundStart>((e,i)=>{_liveAt=float.PositiveInfinity;return HookResult.Continue;});
        RegisterEventHandler<EventRoundFreezeEnd>((e,i)=>{_liveAt=Server.CurrentTime;Log(new{kind="freeze_end",time=_liveAt});return HookResult.Continue;});
        RegisterListener<Listeners.OnMapStart>(map=>{_setup=false;_trial=false;_done=false;_index=0;_variant=0;_selected=[];_readyAt=float.PositiveInfinity;_liveAt=float.PositiveInfinity;
            AddTimer(6,()=>{
                Server.ExecuteCommand("sv_cheats 1;mp_ignore_round_win_conditions 1;bot_join_after_player 0;mp_autoteambalance 0;mp_limitteams 0;bot_kick;bot_join_team ct;bot_quota_mode normal;bot_quota 1;mp_freezetime 1;mp_roundtime_defuse 60;mp_warmup_end;bot_team_nav off;bot_team_support off;mp_restartgame 1");
                _readyAt=Server.CurrentTime+5;
            });});
        RegisterListener<Listeners.OnTick>(Tick);
    }
    private HookResult HoldTrialDestination(DynamicHook hook)
    {
        // A controlled navigation trial, not a natural-match win-rate test.
        // Redirect only this actor's native goal requests to the selected ledge;
        // keep native path planning, view, takeoff, collision and landing intact.
        if(_trial&&hook.GetParam<nint>(0)==_botPointer)hook.SetParam(1,_goal.Handle);
        return HookResult.Continue;
    }
    private static bool Ground(float x,float y,float hint,out float z)
    {
        var a=new Vector(x,y,hint+12);var b=new Vector(x,y,hint-30);
        var hit=Trace.TraceEndShape(a,b,options:new TraceOptions{InteractsWith=Masks.SolidBrushOnly});
        z=a.Z+(b.Z-a.Z)*hit.Fraction;
        if(hit.Fraction<=.01f||hit.Fraction>=.99f||MathF.Abs(z-hint)>18)return false;
        var clear=Trace.TraceEndShape(new(x,y,z+3),new(x,y,z+71),options:new TraceOptions{InteractsWith=Masks.SolidBrushOnly});
        return clear.Fraction>.99f;
    }
    private void SelectCases()
    {
        var expected=_cases.FirstOrDefault(c=>c.Map==Server.MapName)?.SourceMapSha256;
        var archive=Path.GetFullPath(Path.Combine(ModuleDirectory,"../../../../maps",Server.MapName+".vpk"));
        if(string.IsNullOrEmpty(expected)||!File.Exists(archive))
        {_done=true;Log(new{kind="failed",map=Server.MapName,reason="missing-map-provenance"});return;}
        using(var stream=File.OpenRead(archive))
            if(!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).Equals(expected,StringComparison.OrdinalIgnoreCase))
            {_done=true;Log(new{kind="failed",map=Server.MapName,reason="map-checksum-mismatch"});return;}
        foreach(var c in _cases.Where(c=>c.Map==Server.MapName))
        {
            bool valid=Ground(c.Start[0],c.Start[1],c.Start[2],out float start)&&Ground(c.End[0],c.End[1],c.End[2],out _);
            float obstruction=valid?Trace.TraceEndShape(new(c.Start[0],c.Start[1],start+20),new(c.End[0],c.End[1],start+20),options:new TraceOptions{InteractsWith=Masks.SolidBrushOnly}).Fraction:1;
            float headroom=valid?3+157*Trace.TraceEndShape(new(c.Start[0],c.Start[1],start+3),new(c.Start[0],c.Start[1],start+160),options:new TraceOptions{InteractsWith=Masks.SolidBrushOnly}).Fraction:0;
            Log(new{kind="case_geometry",map=Server.MapName,c.Name,c.From,c.To,valid,obstruction,headroom});
            if(!valid||obstruction>.95f)continue;
            if(_selected.Any(s=>MathF.Pow(s.Start[0]-c.Start[0],2)+MathF.Pow(s.Start[1]-c.Start[1],2)<180*180))continue;
            _selected.Add(c);if(_selected.Count==3)break;
        }
        _setup=true;Log(new{kind="selected",map=Server.MapName,cases=_selected.Select(c=>c.Name).ToArray()});
    }
    private static bool Inside(float x,float y,float[][] polygon)
    {
        bool inside=false;
        for(int i=0,j=polygon.Length-1;i<polygon.Length;j=i++)
            if((polygon[i][1]>y)!=(polygon[j][1]>y)&&x<(polygon[j][0]-polygon[i][0])*(y-polygon[i][1])/(polygon[j][1]-polygon[i][1])+polygon[i][0])inside=!inside;
        return inside;
    }
    private void Tick()
    {
        if(_done)return;float now=Server.CurrentTime;if(now<_readyAt)return;
        var rules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault(p=>p.IsValid)?.GameRules;
        if(rules is null||rules.FreezePeriod||rules.WarmupPeriod||now-_liveAt<4.25f)return;
        if(!_setup)SelectCases();
        if(_done)return;
        if(_index>=_selected.Count){FinishMap();return;}
        var c=_selected[_index];
        var actor=Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").FirstOrDefault(p=>p.IsValid&&p.IsBot&&p.PawnIsAlive);
        if(actor?.PlayerPawn.Value is not {IsValid:true} pawn||pawn.Bot is not {} bot||pawn.AbsOrigin is not {} position)return;
        if(!_trial)
        {
            Ground(c.Start[0],c.Start[1],c.Start[2],out _startZ);Ground(c.End[0],c.End[1],c.End[2],out _goalZ);
            float yaw=MathF.Atan2(c.End[1]-c.Start[1],c.End[0]-c.Start[0])*180/MathF.PI;
            pawn.Teleport(new(c.Start[0],c.Start[1],_startZ+2),new QAngle(0,yaw+(_variant==0?0:60),0),new(0,0,0));
            bot.IsCrouching=false;bot.IsRunning=true;
            bot.IsStuck=false;
            bot.RepathTimer.Duration=0;bot.RepathTimer.Timestamp=0;
            _goal=new(c.End[0],c.End[1],_goalZ+1);
            _move!.Invoke(bot.Handle,_goal.Handle,1);
            _trial=true;_botPointer=bot.Handle;_trialAt=_lastMove=now;_nextSample=now;_slot=actor.Slot;_pawn=actor.PlayerPawn.Raw;
            _lastX=c.Start[0];_lastY=c.Start[1];_distance=0;_groundSince=-1;_maxZ=_startZ;_jumps=0;_lastGround=true;
            Log(new{kind="trial_start",map=Server.MapName,c.Name,c.From,c.To,variant=_variant,slot=_slot,pawn=_pawn,start=c.Start,end=c.End,start_z=_startZ,goal_z=_goalZ,time=now,
                freeze=rules.FreezePeriod,warmup=rules.WarmupPeriod,seconds_after_freeze=now-_liveAt});return;
        }
        if(actor.Slot!=_slot||actor.PlayerPawn.Raw!=_pawn){EndTrial(false,"actor-changed",now);return;}
        bool ground=(pawn.Flags&1)!=0;
        if(!ground&&_lastGround&&pawn.AbsVelocity.Z>40)_jumps++;
        _lastGround=ground;_maxZ=MathF.Max(_maxZ,position.Z);
        float dx=position.X-_goal.X,dy=position.Y-_goal.Y;float targetDistance=MathF.Sqrt(dx*dx+dy*dy);
        if((Inside(position.X,position.Y,c.EndCorners)||targetDistance<32)&&MathF.Abs(position.Z-_goalZ)<16&&ground)_groundSince=_groundSince<0?now:_groundSince;
        else _groundSince=-1;
        if(now>=_nextSample)
        {
            _nextSample=now+.05f;_distance+=MathF.Sqrt(MathF.Pow(position.X-_lastX,2)+MathF.Pow(position.Y-_lastY,2));_lastX=position.X;_lastY=position.Y;
            Log(new{kind="sample",map=Server.MapName,c.Name,variant=_variant,time=now,elapsed=now-_trialAt,x=position.X,y=position.Y,z=position.Z,
                vx=pawn.AbsVelocity.X,vy=pawn.AbsVelocity.Y,vz=pawn.AbsVelocity.Z,ground,yaw=pawn.EyeAngles.Y,stuck=bot.IsStuck,
                crouch=bot.IsCrouching,path_index=bot.PathIndex,jump_timestamp=bot.JumpTimestamp,target_distance=targetDistance,
                goal_x=bot.GoalPosition.X,goal_y=bot.GoalPosition.Y,goal_z=bot.GoalPosition.Z,path_view=bot.EyeAnglesUnderPathFinderControl,
                requested_x=new Vector(bot.Handle+0x2e8).X,requested_y=new Vector(bot.Handle+0x2e8).Y,requested_z=new Vector(bot.Handle+0x2e8).Z});
        }
        if(_groundSince>=0&&now-_groundSince>.12f){EndTrial(true,"landed",now);return;}
        if(now-_trialAt>8){EndTrial(false,"timeout",now);return;}
        if(now-_lastMove>=1.5f&&ground){_move!.Invoke(bot.Handle,_goal.Handle,1);_lastMove=now;}
    }
    private void EndTrial(bool success,string reason,float now)
    {
        var c=_selected[_index];Log(new{kind="result",map=Server.MapName,c.Name,variant=_variant,success,reason,seconds=now-_trialAt,jumps=_jumps,max_z=_maxZ,start_z=_startZ,goal_z=_goalZ,distance=_distance});
        _trial=false;_readyAt=now+.4f;if(++_variant==2){_variant=0;_index++;}
    }
    private void FinishMap()
    {
        _done=true;Log(new{kind="map_complete",map=Server.MapName});
        int next=Array.IndexOf(_maps,Server.MapName)+1;
        if(next<_maps.Length)AddTimer(1,()=>Server.ExecuteCommand("changelevel "+_maps[next]));
        else Log(new{kind="complete"});
    }
}
