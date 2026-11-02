using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BotLearning;

// Bounded, session-local round feedback. Inputs are own-team positions/outcomes,
// never enemy coordinates. Pending observations cannot affect the current round.
public sealed class RoundSpatialLearning
{
    private readonly record struct Cell(int Team,int X,int Y,int Z);
    private sealed record Memory(Vector3 Position,float Value);
    private readonly Dictionary<Cell,Memory> _memory=new();
    private readonly Dictionary<Cell,(Vector3 Position,int Count)> _deaths=new();
    private readonly Dictionary<Cell,Vector3> _travel=new();
    private bool _finished=true;
    private uint _anchor;
    private int _anchorTeam;
    public int LearnedRounds {get;private set;}
    public int Regions=>_memory.Count;
    private static Cell Key(int team,Vector3 p)=>new(team,(int)MathF.Floor(p.X/320),(int)MathF.Floor(p.Y/320),(int)MathF.Floor(p.Z/64));
    private static bool Valid(int team,Vector3 p)=>team is 2 or 3&&float.IsFinite(p.X+p.Y+p.Z);
    public void BeginRound(uint anchor=0,int team=0)
    {
        if(anchor!=0&&team is 2 or 3)
        {
            if(_anchor==anchor&&_anchorTeam!=team)Clear();
            _anchor=anchor;_anchorTeam=team;
        }
        _deaths.Clear();_travel.Clear();_finished=false;
    }
    public void Death(int team,Vector3 ownPosition)
    {
        if(_finished||!Valid(team,ownPosition)||_deaths.Count>=256)return;
        var key=Key(team,ownPosition);var previous=_deaths.GetValueOrDefault(key);
        _deaths[key]=(ownPosition,Math.Min(3,previous.Count+1));
    }
    public void Transit(int team,Vector3 ownPosition)
    {
        if(_finished||team!=2||!Valid(team,ownPosition)||_travel.Count>=512)return;
        _travel.TryAdd(Key(team,ownPosition),ownPosition);
    }
    public bool Complete(int winner)
    {
        if(_finished)return false;_finished=true;
        if(winner is not (2 or 3))return false;
        LearnedRounds++;
        foreach(var key in _memory.Keys.ToArray())
        {
            var m=_memory[key];float value=m.Value*.65f;
            if(value<.025f)_memory.Remove(key);else _memory[key]=m with{Value=value};
        }
        foreach(var (key,position) in _travel)
        {
            float old=_memory.GetValueOrDefault(key)?.Value??0;
            float value=key.Team==winner?MathF.Max(0,old-.18f):MathF.Min(1,old+.10f);
            if(value>=.025f)_memory[key]=new(position,value);
        }
        foreach(var (key,death) in _deaths)
        {
            float amount=(key.Team==winner ? .12f : .50f)*death.Count;
            float value=MathF.Min(1,(_memory.GetValueOrDefault(key)?.Value??0)+amount);
            _memory[key]=new(death.Position,value);
        }
        foreach(var key in _memory.OrderByDescending(p=>p.Value.Value).Skip(512).Select(p=>p.Key).ToArray())_memory.Remove(key);
        _deaths.Clear();_travel.Clear();return true;
    }
    public bool HasTeam(int team)=>_memory.Keys.Any(k=>k.Team==team);
    public float Risk(int team,Vector3 point)
    {
        if(!Valid(team,point))return 0;
        var key=Key(team,point);float result=0;
        for(int x=-2;x<=2;x++)for(int y=-2;y<=2;y++)for(int z=-1;z<=1;z++)
        {
            if(!_memory.TryGetValue(key with{X=key.X+x,Y=key.Y+y,Z=key.Z+z},out var m)||MathF.Abs(m.Position.Z-point.Z)>60)continue;
            float dx=point.X-m.Position.X,dy=point.Y-m.Position.Y;
            float distance=MathF.Sqrt(dx*dx+dy*dy);
            result=MathF.Max(result,m.Value*Math.Clamp(1-distance/560,0,1));
        }
        return Math.Clamp(result,0,1);
    }
    public float UtilityMultiplier(int team,Vector3 landing)
        =>team==3&&HasTeam(team)?Math.Clamp(.85f+.40f*Risk(team,landing),.85f,1.25f):1;
    public IReadOnlyList<T> Prioritize<T>(int team,IReadOnlyList<T> choices,Func<T,Vector3> landing,bool explore)
        =>team==3&&!explore&&HasTeam(team)?choices.OrderByDescending(c=>Risk(team,landing(c))).ToArray():choices;
    public void Clear(){_memory.Clear();_deaths.Clear();_travel.Clear();_finished=true;LearnedRounds=0;_anchor=0;_anchorTeam=0;}
}
