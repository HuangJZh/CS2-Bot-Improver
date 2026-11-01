using System.Numerics;
namespace BotAimImprover;

public sealed record HeardSound(uint SourcePawn,string Source,Vector3 Position,float Distance,
    float HeardAt,float Start,float Until,Vector2 Error)
{
    public bool Gunshot => Source=="gunfire";
}

public static class SoundAttention
{
    private static float Score(HeardSound sound,float now) =>
        (sound.Gunshot ? 1.6f : 1f)/MathF.Max(1,sound.Distance/400f)
        *Math.Clamp(1-(now-sound.HeardAt)/.95f,0,1);

    public static HeardSound Merge(HeardSound? current,HeardSound incoming)
    {
        if(current==null || current.Until<=incoming.HeardAt)return incoming;
        if(current.SourcePawn==incoming.SourcePawn && current.Gunshot==incoming.Gunshot)
            return incoming with {Start=MathF.Min(current.Start,incoming.Start),Error=current.Error};
        // Hysteresis avoids alternating between similarly important sources.
        if(incoming.HeardAt-current.HeardAt<.5f && Score(incoming,incoming.HeardAt)<=1.25f*Score(current,incoming.HeardAt))
            return current;
        return incoming with {Start=MathF.Min(current.Start,incoming.Start)};
    }
}
