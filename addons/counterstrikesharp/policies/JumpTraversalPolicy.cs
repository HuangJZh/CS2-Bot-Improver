using System.Numerics;
namespace BotMovement;

// Local native navigation goals only; no enemy positions or invented jump links.
public static class JumpTraversalPolicy
{
    public static bool NearRise(Vector3 feet,Vector3 goal,bool nativePath)
    {
        if(!nativePath||!float.IsFinite(feet.X+feet.Y+feet.Z+goal.X+goal.Y+goal.Z))return false;
        var offset=goal-feet;float horizontal=offset.X*offset.X+offset.Y*offset.Y;
        return horizontal>=16*16&&horizontal<=240*240&&offset.Z>18&&offset.Z<=80;
    }
    // Restore the approach that a native jump started with too little momentum.
    // Bound acceleration, preserve sufficient native momentum, never change Z.
    public static Vector2 AirStep(Vector2 position,Vector2 velocity,Vector2 landing,float seconds)
    {
        if(!float.IsFinite(position.X+position.Y+velocity.X+velocity.Y+landing.X+landing.Y+seconds)
            ||seconds<=0||seconds>.05f)return velocity;
        var offset=landing-position;float distance=offset.Length();
        if(distance<16||distance>260)return velocity;
        var direction=offset/distance;
        float wish=Math.Clamp((distance-8)/.25f,60,180);
        float along=Vector2.Dot(velocity,direction);
        if(along>=wish&&(velocity-direction*along).Length()<45)return velocity;
        var delta=direction*wish-velocity;float maxChange=900*seconds;
        if(delta.Length()>maxChange)delta=Vector2.Normalize(delta)*maxChange;
        return velocity+delta;
    }
}
