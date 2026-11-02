using System.Numerics;
namespace BotAimImprover;

public sealed class NavigationArea
{
    public int Id {get;set;}
    public float[] Position {get;set;}=[];
    public int[] Neighbors {get;set;}=[];
    public float[][] Corners {get;set;}=[];
    public Vector3 Point=>new(Position[0],Position[1],Position[2]);
}
public sealed class NavigationMap
{
    public string Map {get;set;}="";
    public string SourceMapSha256 {get;set;}="";
    public string SourceNavSha256 {get;set;}="";
    public List<NavigationArea> Areas {get;set;}=[];
}

// Tactical routes over the installed map's directed native adjacency graph.
// Native MoveTo remains responsible for collision, stairs, ladders and steering.
public sealed class TeamNavigationGraph
{
    public IReadOnlyDictionary<int,NavigationArea> Areas {get;}
    public TeamNavigationGraph(IEnumerable<NavigationArea> areas)
    {
        var nodes=areas.ToArray();
        if(nodes.Length is 0 or >20000||nodes.Any(a=>a.Position.Length!=3||!Finite(a.Point)
            ||a.Corners.Length<3||a.Corners.Any(c=>c.Length!=3||!float.IsFinite(c.Sum()))))
            throw new ArgumentException("Invalid navigation graph");
        Areas=nodes.ToDictionary(a=>a.Id);
        if(nodes.Any(a=>a.Neighbors.Any(n=>!Areas.ContainsKey(n))))throw new ArgumentException("Dangling navigation edge");
    }
    private static bool Finite(Vector3 p)=>float.IsFinite(p.X+p.Y+p.Z);
    private static bool Contains(NavigationArea a,Vector3 point)
    {
        bool inside=false;var poly=a.Corners;
        for(int i=0,j=poly.Length-1;i<poly.Length;j=i++)
            if((poly[i][1]>point.Y)!=(poly[j][1]>point.Y)
                &&point.X<(poly[j][0]-poly[i][0])*(point.Y-poly[i][1])/(poly[j][1]-poly[i][1])+poly[i][0])inside=!inside;
        return inside;
    }
    public int? Locate(Vector3 point,float maxDistance=320)
    {
        if(!Finite(point))return null;
        var containing=Areas.Values.Where(a=>Contains(a,point)&&MathF.Abs(a.Point.Z-point.Z)<120)
            .OrderBy(a=>MathF.Abs(a.Point.Z-point.Z)).FirstOrDefault();
        if(containing!=null)return containing.Id;
        var nearest=Areas.Values.Where(a=>MathF.Abs(a.Point.Z-point.Z)<120)
            .OrderBy(a=>Vector3.DistanceSquared(point,a.Point)).FirstOrDefault();
        return nearest!=null&&Vector3.Distance(point,nearest.Point)<=maxDistance?nearest.Id:null;
    }
    public List<int>? Path(int start,int goal,IReadOnlyDictionary<int,int>? occupied=null,IReadOnlyDictionary<int,float>? learnedRisk=null)
    {
        if(!Areas.ContainsKey(start)||!Areas.ContainsKey(goal))return null;
        var queue=new PriorityQueue<int,float>();var costs=new Dictionary<int,float>{{start,0}};
        var parents=new Dictionary<int,int>();var closed=new HashSet<int>();queue.Enqueue(start,0);
        while(queue.TryDequeue(out int current,out _))
        {
            if(!closed.Add(current))continue;
            if(current==goal)
            {
                var result=new List<int>{goal};
                while(current!=start){current=parents[current];result.Add(current);if(result.Count>4096)return null;}
                result.Reverse();return result;
            }
            foreach(int next in Areas[current].Neighbors)
            {
                float distance=MathF.Max(1,Vector3.Distance(Areas[current].Point,Areas[next].Point));
                // Avoid stacking every squad on one corridor; still require reachability.
                float risk=Math.Clamp(learnedRisk?.GetValueOrDefault(next)??0,0,1);
                float cost=costs[current]+distance*(1+1.5f*(occupied?.GetValueOrDefault(next)??0)+2*risk);
                if(cost>=costs.GetValueOrDefault(next,float.PositiveInfinity))continue;
                costs[next]=cost;parents[next]=current;
                queue.Enqueue(next,cost+Vector3.Distance(Areas[next].Point,Areas[goal].Point));
            }
        }
        return null;
    }
    public float Length(IReadOnlyList<int> path)
    {float length=0;for(int i=1;i<path.Count;i++)length+=Vector3.Distance(Areas[path[i-1]].Point,Areas[path[i]].Point);return length;}
    public List<int>? DiversifiedPath(int start,int goal,IReadOnlyDictionary<int,int> occupied,IReadOnlyDictionary<int,float>? learnedRisk=null)
    {
        var shortest=Path(start,goal);if(shortest==null)return null;
        var alternative=Path(start,goal,occupied,learnedRisk);
        return alternative!=null&&Length(alternative)<=Length(shortest)*1.4f+64?alternative:shortest;
    }
}

public readonly record struct NavigationContext(bool Combat,bool Busy,bool Grounded,bool FriendlyBlocked,
    bool RoundLive,bool NativeOwned,bool Urgent,float Elapsed,float Deadline);
public static class TeamNavigationPolicy
{
    public static int ChooseSite(int team,int attackSite,Vector3 origin,IReadOnlyList<Vector3> sites,IReadOnlyList<int> assigned)
    {
        if(sites.Count==0||sites.Count!=assigned.Count)throw new ArgumentException("Missing site assignments");
        return team==2?attackSite:Enumerable.Range(0,sites.Count).MinBy(i=>Vector3.Distance(origin,sites[i])+assigned[i]*1800);
    }
    public static bool CanDrive(NavigationContext c)=>c.RoundLive&&!c.Combat&&!c.Busy&&c.Grounded
        &&!c.FriendlyBlocked&&!c.NativeOwned&&!c.Urgent&&float.IsFinite(c.Elapsed+c.Deadline)&&c.Elapsed<c.Deadline;
    // Same-team geometry only; never query unseen opponents to choose an opening.
    public static bool TooCloseAhead(Vector3 origin,Vector3 next,Vector3 mate)
    {
        var direction=next-origin;direction.Z=0;var offset=mate-origin;
        if(direction.LengthSquared()<1||MathF.Abs(offset.Z)>48)return false;
        float ahead=Vector3.Dot(offset,Vector3.Normalize(direction));
        var side=offset-Vector3.Normalize(direction)*ahead;side.Z=0;
        return ahead>0&&ahead<105&&side.Length()<38;
    }
}
