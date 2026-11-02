using System.Numerics;
namespace BotAimImprover;

public static class TeamSupportPolicy
{
    public static bool Candidate(Vector3 position,Vector3 anchor,bool available)
    {
        float d=Vector3.Distance(position,anchor);
        return available&&float.IsFinite(d)&&MathF.Abs(position.Z-anchor.Z)<=100&&d is >=120 and <=850;
    }
    public static bool Route(float length,float direct)
        =>float.IsFinite(length+direct)&&length>0&&length<=1000&&length<=direct*1.6f+96;
    public static bool Continue(float now,float started,float lastProgress,float distance,bool available)
        =>available&&now>=started&&now-started<2.5f&&now-lastProgress<.9f&&distance>100;
}
