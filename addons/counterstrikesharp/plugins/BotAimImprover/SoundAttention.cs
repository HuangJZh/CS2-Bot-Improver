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
        if(current.SourcePawn==incoming.SourcePawn)
            return incoming with {Source=current.Gunshot?"gunfire":incoming.Source,
                Start=MathF.Min(current.Start,incoming.Start),Error=current.Error};
        // Hysteresis avoids alternating between similarly important sources.
        if(incoming.HeardAt-current.HeardAt<.5f && Score(incoming,incoming.HeardAt)<=1.25f*Score(current,incoming.HeardAt))
            return current;
        // A different source has its own reaction deadline; it cannot borrow the
        // already-paid reaction of the previous sound and cause an instant flip.
        return incoming;
    }
}

public sealed class SoundGazeFocus
{
    public HeardSound? Cue {get;private set;}
    public float ReadyAt {get;private set;}
    public float Until {get;private set;}
    private float _began,_restUntil;
    private uint _restSource;
    private bool _restGunshot;
    public bool Holding(float now)=>Cue!=null&&now<Until;
    public bool Ready(float now)=>Holding(now)&&now>=ReadyAt;
    public void End(float now)
    {
        if(Cue is {} cue){_restSource=cue.SourcePawn;_restGunshot=cue.Gunshot;_restUntil=now+.7f;}
        Cue=null;
    }
    public bool Select(HeardSound incoming,float now)
    {
        if(Holding(now)&&Cue!.SourcePawn==incoming.SourcePawn)
        {
            Cue=incoming;ReadyAt=MathF.Min(ReadyAt,incoming.Start);
            Until=MathF.Min(_began+1.6f,MathF.Max(Until,incoming.Until));
            return true;
        }
        if(Cue!=null&&!Holding(now))End(now);
        if(Cue==null&&incoming.SourcePawn==_restSource&&now<_restUntil&&(!incoming.Gunshot||_restGunshot))return false;
        if(incoming.Until<=now)return Holding(now);
        Cue=incoming;_began=now;ReadyAt=incoming.Start;
        Until=MathF.Min(_began+1.6f,MathF.Max(incoming.Until,ReadyAt+.65f));
        return true;
    }
}
