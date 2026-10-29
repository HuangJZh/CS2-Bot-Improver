namespace BotTeamplay;

public enum TeamOrder { None, Weapon, Follow, ScoutA, ScoutB, Cancel }

public readonly record struct BotCandidate(int Slot, int Money, double Distance, bool Alive,
    bool Busy, bool InBuyZone, bool HasPrimary);

// Pure selection rules: runtime adapters are responsible for same-team filtering.
public static class TeamplayPolicy
{
    public static TeamOrder Parse(string token) => token.Trim().ToLowerInvariant() switch
    {
        "cw.needweapon" or "#chatwheel_requestweapon" or "#chatwheel_needrop" or "needrop" => TeamOrder.Weapon,
        "cw.followme" or "#chatwheel_followme" or "followme" => TeamOrder.Follow,
        "cw.goa" or "cw.aplan" or "#chatwheel_aplan" or "#chatwheel_a" or "go_a" => TeamOrder.ScoutA,
        "cw.gob" or "cw.bplan" or "#chatwheel_bplan" or "#chatwheel_b" or "go_b" => TeamOrder.ScoutB,
        "cancel" => TeamOrder.Cancel,
        _ => TeamOrder.None
    };

    public static BotCandidate? Donor(IEnumerable<BotCandidate> candidates, int price, int reserve,
        double maxDistance) => price <= 0 || reserve < 0 ? null : candidates
        .Where(b => b.Alive && !b.Busy && b.InBuyZone && b.HasPrimary && double.IsFinite(b.Distance)
            && b.Distance <= maxDistance && b.Money >= (long)price + reserve)
        .OrderBy(b => b.Distance).ThenByDescending(b => b.Money).ThenBy(b => b.Slot)
        .Select(b => (BotCandidate?)b).FirstOrDefault();

    public static int[] Responders(IEnumerable<BotCandidate> candidates, int maxResponders,
        double maxDistance) => candidates.Where(b => b.Alive && !b.Busy && double.IsFinite(b.Distance)
            && b.Distance <= maxDistance).OrderBy(b => b.Distance).ThenBy(b => b.Slot)
        .Take(Math.Clamp(maxResponders, 0, 2)).Select(b => b.Slot).ToArray();

    public static bool CoolingDown(double now, double last, double duration) =>
        !double.IsFinite(now) || !double.IsFinite(last) || now < last || now - last < duration;
}
