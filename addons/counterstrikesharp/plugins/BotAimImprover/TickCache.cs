namespace BotAimImprover;

// Cache discovery only, never live pawn state, coordinates or visibility.
// Lifecycle events invalidate even within the same tick (slot reuse / map change).
internal sealed class TickCache<T>
{
    private bool _valid;
    private int _tick;
    private T _value=default!;
    public void Invalidate()=>_valid=false;
    public T Get(int tick,Func<T> read)
    {
        if(_valid&&_tick==tick)return _value;
        var value=read(); // A failed discovery must not mark the cache valid.
        _value=value;_tick=tick;_valid=true;
        return value;
    }
}
