using System.Numerics;
namespace BotAimImprover;
internal static class SmokeAimPolicy
{
    internal static float Radius(float age) => age is < 0 or >= 20 ? 0 : 155f*Math.Clamp(age/.7f,0,1);
    internal static float Error(float distance) => Math.Clamp(distance*.065f,35,100);
    internal static bool Blocks(Vector3 eye,Vector3 target,Vector3 center,float radius)
    {
        if (radius<=0 || !float.IsFinite(radius)) return false;
        var delta=target-eye; var length=delta.LengthSquared();
        float t=length>0 ? Math.Clamp(Vector3.Dot(center-eye,delta)/length,0,1) : 0;
        return Vector3.DistanceSquared(eye+delta*t,center)<=radius*radius;
    }
}
