using BotAimImprover;
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;

int checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);checks++;}
var cache=new TickCache<object>();int reads=0;
object Read(){reads++;return new();}
var first=cache.Get(0,Read);
for(int i=0;i<100;i++)Check(ReferenceEquals(first,cache.Get(0,Read)),"same tick discovery");
Check(reads==1,"only one discovery per tick");
cache.Invalidate();Check(!ReferenceEquals(first,cache.Get(0,Read)),"same-tick entity replacement invalidates");
Check(!ReferenceEquals(first,cache.Get(-1,Read))&&reads==3,"tick rewind/map change refreshes");
cache.Invalidate();try{cache.Get(-1,()=>throw new InvalidOperationException());}catch(InvalidOperationException){}
cache.Get(-1,Read);Check(reads==4,"failed discovery is retried");
// The cleanup loops rely on the target runtime's supported removal semantics.
var d=Enumerable.Range(0,10).ToDictionary(i=>i);
foreach(var (key,_) in d)if(key%2==0)d.Remove(key);
Check(d.Count==5,"dictionary cleanup preserves remaining entries without snapshots");
var geometryRandom=new Random(850);
for(int i=0;i<10000;i++)
{
 float x=geometryRandom.Next(-10000,10001),y=geometryRandom.Next(-10000,10001),z=geometryRandom.Next(-1000,1001);
 float eyeZ=geometryRandom.Next(40,65),yaw=geometryRandom.Next(-180,181),frac=geometryRandom.NextSingle(),lat=geometryRandom.Next(-10,11);
 bool feet=i%17==0;if(feet)frac=5;
 var geo=new AimTargetGeometry(x,y,z,eyeZ,yaw);
 Check(geo.TryPoint(frac,lat,feet,out float px,out float py,out float pz),"valid geometry");
 float rx=(float)Math.Sin(yaw*Math.PI/180.0),ry=(float)-Math.Cos(yaw*Math.PI/180.0);
 Check(px==(feet?x:x+rx*lat)&&py==(feet?y:y+ry*lat)&&pz==(feet?z+frac:z+eyeZ*frac),"sample coordinates match original transform");
}

NavigationArea N(int id,float x,float y,float z)=>new(){Id=id,Position=[x,y,z],Corners=[[x-4,y-4,z],[x+4,y-4,z],[x+4,y+4,z],[x-4,y+4,z]]};
var ties=new TeamNavigationGraph([N(20,-256,0,0),N(10,256,0,0),N(30,-256,0,20)]);
Check(ties.Locate(Vector3.Zero,700)==20,"equidistant candidates retain original graph order across cells");
Check(ties.Locate(new(-256,0,10),0)==20,"overlapping floors retain stable height ties");
var huge=N(1,0,0,0);huge.Corners=[[-1e8f,-1e8f,0],[1e8f,-1e8f,0],[1e8f,1e8f,0],[-1e8f,1e8f,0]];
var fallback=new TeamNavigationGraph([huge,N(2,1e8f+100,0,0),N(3,1e8f+200,0,0)]);
Check(fallback.Locate(new(1e7f,0,0),0)==1,"oversized index falls back without losing containment");
Check(fallback.Locate(new(1e8f+150,0,0),1000)==Original(fallback,new(1e8f+150,0,0),1000),"fallback retains ties after the oversized node");

static bool Contains(NavigationArea a,Vector3 p)
{
 bool inside=false;var poly=a.Corners;
 for(int i=0,j=poly.Length-1;i<poly.Length;j=i++)
  if((poly[i][1]>p.Y)!=(poly[j][1]>p.Y)&&p.X<(poly[j][0]-poly[i][0])*(p.Y-poly[i][1])/(poly[j][1]-poly[i][1])+poly[i][0])inside=!inside;
 return inside;
}
// Exact pre-optimization implementation, retained here solely as an oracle.
static int? Original(TeamNavigationGraph graph,Vector3 p,float radius)
{
 if(!float.IsFinite(p.X+p.Y+p.Z))return null;
 var containing=graph.Areas.Values.Where(a=>Contains(a,p)&&MathF.Abs(a.Point.Z-p.Z)<120).OrderBy(a=>MathF.Abs(a.Point.Z-p.Z)).FirstOrDefault();
 if(containing!=null)return containing.Id;
 var nearest=graph.Areas.Values.Where(a=>MathF.Abs(a.Point.Z-p.Z)<120).OrderBy(a=>Vector3.DistanceSquared(p,a.Point)).FirstOrDefault();
 return nearest!=null&&Vector3.Distance(p,nearest.Point)<=radius?nearest.Id:null;
}
static (double Ms,long Bytes,long Checksum) Measure(Func<long> work)
{
 long allocated=GC.GetAllocatedBytesForCurrentThread(),start=Stopwatch.GetTimestamp();long sum=work();
 return(Stopwatch.GetElapsedTime(start).TotalMilliseconds,GC.GetAllocatedBytesForCurrentThread()-allocated,sum);
}
var results=new List<object>();
var folder=args.Length>0?args[0]:"addons/counterstrikesharp/plugins/BotAimImprover/navigation";
foreach(var file in Directory.GetFiles(folder,"*.json").Order())
{
 var data=JsonSerializer.Deserialize<NavigationMap>(File.ReadAllText(file))!;
 var graph=new TeamNavigationGraph(data.Areas);var random=new Random(711);
 var queries=new List<(Vector3 Point,float Radius)>();
 float[] radii=[0,100,320,700,-1,float.NaN,float.PositiveInfinity];
 for(int i=0;i<1400;i++)
 {
  var a=data.Areas[random.Next(data.Areas.Count)];
  var p=i%3==0?new Vector3(a.Corners[0][0],a.Corners[0][1],a.Corners[0][2]):a.Point+new Vector3(random.Next(-800,801),random.Next(-800,801),random.Next(-200,201));
  float radius=radii[i%radii.Length];
  Check(graph.Locate(p,radius)==Original(graph,p,radius),$"{data.Map} mismatch at {p} r={radius}");
  if(float.IsFinite(radius)&&radius>=0)queries.Add((p,radius));
 }
 foreach(var p in new[]{new Vector3(float.NaN,0,0),new Vector3(float.PositiveInfinity,0,0),new Vector3(1e30f,1e30f,0)})
  Check(graph.Locate(p)==Original(graph,p,320),"non-map point fallback");
 long Run(bool old){long sum=0;for(int repeat=0;repeat<3;repeat++)foreach(var q in queries)sum+=(old?Original(graph,q.Point,q.Radius):graph.Locate(q.Point,q.Radius))??-1;return sum;}
 Run(false);Run(true); // JIT and tiering warm-up; no cold-load claim.
 var before=Measure(()=>Run(true));var after=Measure(()=>Run(false));
 Check(before.Checksum==after.Checksum,"benchmark results unchanged");
 results.Add(new{map=data.Map,areas=data.Areas.Count,queries=queries.Count*3,before_ms=before.Ms,after_ms=after.Ms,before_bytes=before.Bytes,after_bytes=after.Bytes});
}
Console.WriteLine(JsonSerializer.Serialize(new{checks,scope="Offline navigation microbenchmark, not game FPS or whole-plugin CPU",results},new JsonSerializerOptions{WriteIndented=true}));
