namespace BotState;
public enum PostureUpdate { Unowned, Stand, Crouch, Release }
// Only decision coherence changes here. Existing weapon probabilities are preserved.
public sealed class BurstPosturePolicy
{
    private sealed record Decision(uint Pawn,string Weapon,double LastShot,bool Crouch);
    private readonly Dictionary<int,Decision> _decisions=new();
    public bool OnShot(int player,uint pawn,string weapon,double now,double chance,Func<double> draw)
    {
        if(!double.IsFinite(now))return false;
        if(!_decisions.TryGetValue(player,out var state) || state.Pawn!=pawn || state.Weapon!=weapon
            || now<state.LastShot || now-state.LastShot>.5)
            state=new(pawn,weapon,now,draw()<Math.Clamp(chance,0,1));
        _decisions[player]=state with {LastShot=now};
        return state.Crouch;
    }
    public PostureUpdate Update(int player,uint pawn,double now,bool attacking)
    {
        if(!_decisions.TryGetValue(player,out var state))return PostureUpdate.Unowned;
        if(state.Pawn!=pawn) { _decisions.Remove(player);return PostureUpdate.Unowned; }
        if(!attacking || !double.IsFinite(now) || now<state.LastShot || now-state.LastShot>.5)
        { _decisions.Remove(player);return PostureUpdate.Release; }
        return state.Crouch ? PostureUpdate.Crouch : PostureUpdate.Stand;
    }
    public void Clear()=>_decisions.Clear();
}
