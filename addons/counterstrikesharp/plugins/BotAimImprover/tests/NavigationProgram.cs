using BotAimImprover;
using System.Numerics;
using System.Text.Json;
int checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);checks++;Console.WriteLine("PASS "+name);}
NavigationArea Node(int id,float x,float y,float z,params int[] neighbors)=>new(){Id=id,Position=[x,y,z],Neighbors=neighbors,
    Corners=[[x-8,y-8,z],[x+8,y-8,z],[x+8,y+8,z],[x-8,y+8,z]]};
var graph=new TeamNavigationGraph([Node(0,0,0,0,1,2),Node(1,100,80,0,3),Node(2,100,-80,0,4),Node(3,200,80,0,5),Node(4,200,-80,0,5),Node(5,300,0,0)]);
var first=graph.Path(0,5)!;var used=first.ToDictionary(i=>i,i=>1);var second=graph.DiversifiedPath(0,5,used)!;
Check(first.First()==0&&first.Last()==5,"native graph endpoints are preserved");
Check(first.Skip(1).Take(first.Count-2).Intersect(second.Skip(1).Take(second.Count-2)).Count()==0,"squad routes can use different reachable corridors");
Check(graph.Length(second)<=graph.Length(first)*1.4f+64,"alternate route respects bounded detour");
Check(graph.Path(5,0)==null,"directed drops or connections are not silently reversed");
Check(graph.Path(0,999)==null,"missing destination yields native fallback");
var stacked=new TeamNavigationGraph([Node(1,0,0,0),Node(2,0,0,200)]);
Check(stacked.Locate(new(0,0,200))==2&&stacked.Locate(Vector3.Zero)==1,"stacked floors use height rather than XY alone");
Check(stacked.Locate(new(2000,2000,0))==null,"distant points cannot teleport onto a graph");
Check(stacked.Locate(new(float.NaN,0,0))==null,"invalid coordinates cannot enter path planning");
var detour=new TeamNavigationGraph([Node(0,0,0,0,1,2),Node(1,100,0,0,3),Node(2,0,1000,0,3),Node(3,200,0,0)]);
Check(detour.DiversifiedPath(0,3,new Dictionary<int,int>{{1,100}})!.Contains(1),"crowd penalty cannot force an excessive detour");
bool rejected=false;try{_ =new TeamNavigationGraph([Node(1,0,0,0,999)]);}catch(ArgumentException){rejected=true;}
Check(rejected,"invalid adjacency data fails closed");
var context=new NavigationContext(false,false,true,false,true,false,false,2,35);
Check(TeamNavigationPolicy.CanDrive(context),"opening transit allows planning");
Check(!TeamNavigationPolicy.CanDrive(context with{Combat=true}),"visible combat yields native decisions");
Check(!TeamNavigationPolicy.CanDrive(context with{Busy=true}),"utility and objective actions yield native decisions");
Check(!TeamNavigationPolicy.CanDrive(context with{Urgent=true}),"urgent bomb timing overrides route diversity");
Check(!TeamNavigationPolicy.CanDrive(context with{Grounded=false}),"jumping and ladders retain native steering");
Check(!TeamNavigationPolicy.CanDrive(context with{NativeOwned=true}),"another controller retains ownership");
Check(!TeamNavigationPolicy.CanDrive(context with{Elapsed=35}),"navigation has a finite lease");
Check(TeamNavigationPolicy.TooCloseAhead(Vector3.Zero,new(1000,0,0),new(70,5,0)),"same-corridor teammate spacing is detected");
Check(!TeamNavigationPolicy.TooCloseAhead(Vector3.Zero,new(1000,0,0),new(-70,0,0)),"teammate behind does not block entry");
Check(!TeamNavigationPolicy.TooCloseAhead(Vector3.Zero,new(1000,0,0),new(70,0,200)),"another floor does not trigger spacing");
var sites=new Vector3[]{new(1000,0,0),new(-1000,0,0)};
Check(TeamNavigationPolicy.ChooseSite(3,0,Vector3.Zero,sites,new[]{2,0})==1,"defenders account for already assigned teammates");
Check(TeamNavigationPolicy.ChooseSite(2,0,Vector3.Zero,sites,new[]{4,0})==0,"attackers keep a shared objective rather than split across both sites");
foreach(var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory,"navigation"),"*.json"))
{
 var data=JsonSerializer.Deserialize<NavigationMap>(File.ReadAllText(file))!;var live=new TeamNavigationGraph(data.Areas);
 int reached=0;var rng=new Random(8721);var keys=live.Areas.Keys.ToArray();
 for(int i=0;i<80;i++)
 {
  int start=keys[rng.Next(keys.Length)],goal=keys[rng.Next(keys.Length)];var path=live.Path(start,goal);if(path==null)continue;reached++;
  for(int j=1;j<path.Count;j++)if(!live.Areas[path[j-1]].Neighbors.Contains(path[j]))throw new Exception("Non-native connection");
  var alternative=live.DiversifiedPath(start,goal,path.ToDictionary(n=>n,n=>1))!;
  if(live.Length(alternative)>live.Length(path)*1.4f+64.01f)throw new Exception("Excessive detour");
 }
 Check(reached>30,data.Map+" sampled native routes retain legal edges and bounded alternatives");
}
Console.WriteLine($"{checks} navigation checks passed");
