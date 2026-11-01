namespace BotAimImprover;

public readonly record struct TacticalMovementContext(bool Combat,bool BusyObjective,bool Frozen,bool CounterTerrorist,
    bool CarryingBomb,float RoundSeconds,bool BombPlanted,float BombSeconds,float BombDistance,float DefuseSeconds);
public readonly record struct TacticalMovementDecision(bool Quiet,bool Peek,string Reason);

public static class TacticalMovementPolicy
{
    public static TacticalMovementDecision Evaluate(TacticalMovementContext c)
    {
        if(!float.IsFinite(c.RoundSeconds+c.BombSeconds+c.BombDistance+c.DefuseSeconds))return new(false,false,"unknown-context");
        if(c.Frozen)return new(false,false,"round-not-live");
        if(c.BusyObjective)return new(false,false,"objective-action");
        if(c.Combat)return new(false,false,"combat");
        if(c.BombPlanted)
        {
            float travel=MathF.Max(0,c.BombDistance)/220f;
            if(c.CounterTerrorist && c.BombSeconds<=travel+c.DefuseSeconds+6)
                return new(false,false,"urgent-defuse");
            if(!c.CounterTerrorist && c.BombSeconds<=8 && c.BombDistance<1400)
                return new(false,false,"bomb-escape");
            return new(true,true,c.CounterTerrorist?"retake-approach":"postplant-cover");
        }
        if(!c.CounterTerrorist && c.CarryingBomb && c.RoundSeconds<=18)
            return new(false,false,"urgent-plant");
        return new(true,true,"normal-approach");
    }
}
