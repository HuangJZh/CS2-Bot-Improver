namespace BotAimImprover;

// Belongs to a shooter and weapon, not to the current enemy's visual model.
public sealed class CombatHistory
{
    public float LastShotAt=-999,UnderFireUntil, SprayBasePitchError;
    public uint LastTarget=uint.MaxValue;
    public BurstCadence Cadence {get;}=new();
    public int SprayShots=>Cadence.Shots;
    public float BurstCooldownUntil=>Cadence.PauseUntil;
    public void Shot(float now,uint target,int length,float pause)
    {Cadence.Shot(now,length,pause);LastShotAt=now;LastTarget=target;}
}

public sealed class VisibleTargetHistory
{
    private readonly Dictionary<uint,(float First,float Last)> _seen=new();
    public void Notice(uint target,float now)
    {
        if(!float.IsFinite(now))return;
        foreach(var key in _seen.Where(p=>now-p.Value.Last>.5f).Select(p=>p.Key).ToArray())_seen.Remove(key);
        _seen[target]=_seen.TryGetValue(target,out var seen)&&now-seen.Last<=.2f?(seen.First,now):(now,now);
    }
    public float ReactionCredit(uint target,float now,HumanAimProfile profile)
        =>_seen.TryGetValue(target,out var seen)&&now>=seen.Last&&now-seen.Last<=.2f
            ?Math.Clamp(now-seen.First,0,profile.Reaction*.75f):0;
}
