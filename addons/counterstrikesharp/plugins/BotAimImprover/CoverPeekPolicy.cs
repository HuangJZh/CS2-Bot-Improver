using System.Numerics;
namespace BotAimImprover;

public readonly record struct PeekCandidate(Vector3 Destination,bool PathClear,bool GroundSafe,bool RevealsAngle);
public readonly record struct PeekRoute(int PathIndex,Vector3 Origin,Vector3 Goal,Vector3 CheckPoint,Vector3 Destination);

// A local clearance route, bounded by a native navigation segment. Geometry is
// supplied by world traces; no live/hidden enemy position enters the planner.
public static class CoverPeekPolicy
{
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X+v.Y+v.Z);
    public static PeekRoute? Plan(int pathIndex,Vector3 origin,Vector3 goal,Vector3 checkPoint,
        bool currentAngleBlocked,bool allowed,IEnumerable<PeekCandidate> candidates)
    {
        if(!Finite(origin)||!Finite(goal)||!Finite(checkPoint))return null;
        var forward=goal-origin;forward.Z=0;
        if(!allowed||!currentAngleBlocked||pathIndex<0||forward.Length()<64)return null;
        forward=Vector3.Normalize(forward);
        PeekCandidate? best=null;float score=float.MaxValue;
        foreach(var c in candidates)
        {
            if(!Finite(c.Destination))continue;
            var step=c.Destination-origin;step.Z=0;
            float distance=step.Length();
            if(!c.PathClear||!c.GroundSafe||!c.RevealsAngle||distance<8||distance>52
                ||Vector3.Dot(step,forward)<0)continue;
            float cost=distance-Vector3.Dot(step,forward)*.2f;
            if(cost<score){score=cost;best=c;}
        }
        return best is {} chosen?new(pathIndex,origin,goal,checkPoint,chosen.Destination):null;
    }
    public static bool Cancel(PeekRoute route,int pathIndex,Vector3 position,Vector3 goal,bool allowed,float elapsed)
        =>!allowed||!Finite(position)||!Finite(goal)||!float.IsFinite(elapsed)||elapsed<0||pathIndex!=route.PathIndex||elapsed>=.18f
            ||Vector3.Distance(goal,route.Goal)>128||Vector3.Distance(position,route.Origin)>44;
}
