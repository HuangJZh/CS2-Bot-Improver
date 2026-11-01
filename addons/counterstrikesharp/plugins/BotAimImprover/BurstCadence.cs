namespace BotAimImprover;

public sealed class BurstCadence
{
    public int Shots {get;private set;}
    public float PauseUntil {get;private set;}=float.NegativeInfinity;
    private float _lastShot=float.NegativeInfinity;
    public bool Paused(float now)=>now<PauseUntil;
    public void Shot(float now,int length,float pause)
    {
        if(!float.IsFinite(now+pause) || length<1 || pause<=0)throw new ArgumentException("Invalid burst");
        // A queued shot during the pause must not extend its deadline forever.
        if(Paused(now)){Shots++;_lastShot=now;return;}
        if(now-_lastShot>=.25f || PauseUntil>float.NegativeInfinity)Shots=0;
        PauseUntil=float.NegativeInfinity;
        Shots++;_lastShot=now;
        if(Shots>=length)PauseUntil=now+Math.Clamp(pause,.05f,1f);
    }
}
