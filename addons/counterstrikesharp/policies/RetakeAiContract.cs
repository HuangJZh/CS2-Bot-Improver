namespace BotCombatPolicy;

public static class RetakeAiContract
{
    public const string Tree="scripts/ai/custom_improver/retake.kv3";
    public static bool Eligible(bool enabled,int type,int mode,bool planted,bool roundLive)
        =>enabled&&type==0&&mode is 0 or 1&&planted&&roundLive;
    public static uint RememberTarget(uint observed,uint previous,bool previousAlive)
        =>observed!=uint.MaxValue?observed:previousAlive?previous:uint.MaxValue;
    public static bool NeedsWeaponClock(bool retake,long suppressionToken)=>!retake||suppressionToken<=0;
}

// Trigger recovery only after a continuously authorized, weapon-ready stall.
// It cannot grant permission, skip a reaction window or survive an identity change.
public sealed class RetakeShotWatchdog
{
    private (uint Pawn,uint Weapon,uint Target) _owner;
    private float? _readySince;
    private float _shot=float.NegativeInfinity,_last=float.NegativeInfinity,_next;
    public bool Update(uint pawn,uint weapon,uint target,float now,bool ready,float lastShot)
    {
        var owner=(pawn,weapon,target);
        if(owner!=_owner||now<_last||lastShot>_shot){_owner=owner;_readySince=null;_next=now;}
        _last=now;_shot=lastShot;
        if(!ready){_readySince=null;return false;}
        _readySince??=now;
        if(now-_readySince.Value<.6f||now<_next)return false;
        _readySince=now;_next=now+1;return true;
    }
}
