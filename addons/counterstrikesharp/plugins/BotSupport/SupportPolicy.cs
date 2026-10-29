namespace BotSupport;

public readonly record struct Gun(string Item, string Label, int Price);
public readonly record struct Donor(int Slot, int Money, double Distance);
public static class SupportPolicy
{
    public static int StopChance(string difficulty) => difficulty switch { "Low" => 75, "High" => 95, _ => 85 };
    public static bool StopSucceeds(string difficulty, int roll) => roll >= 0 && roll < StopChance(difficulty);
    public static bool IsWeaponRequest(string token) => token.Trim().ToLowerInvariant() is
        "cw.needweapon" or "cw.needrop" or "cw.needdrop" or "#chatwheel_requestweapon" or "#chatwheel_needrop" or "needrop";
    public static Gun[] Guns(int team, string request) => request.ToLowerInvariant() switch
    {
        "awp" or "awm" => [new("weapon_awp", "AWP", 4750)],
        "ak" or "ak47" when team == 2 => [new("weapon_ak47", "AK-47", 2700)],
        "m4a1" or "m4a1_silencer" when team == 3 => [new("weapon_m4a1_silencer", "M4A1-S", 2900)],
        "m4a4" when team == 3 => [new("weapon_m4a1", "M4A4", 2900)],
        "auto" when team == 2 => [new("weapon_awp", "AWP", 4750), new("weapon_ak47", "AK-47", 2700)],
        "auto" when team == 3 => [new("weapon_awp", "AWP", 4750), new("weapon_m4a1_silencer", "M4A1-S", 2900), new("weapon_m4a1", "M4A4", 2900)],
        _ => []
    };
    public static (Donor Donor, Gun Gun)? Select(IEnumerable<Donor> donors, int team, string request)
    {
        var candidates = donors.Where(d => double.IsFinite(d.Distance) && d.Distance <= 1500).ToArray();
        foreach (var gun in Guns(team, request))
        {
            var donor = candidates.Where(d => d.Money >= gun.Price + 800)
                .OrderByDescending(d => d.Money).ThenBy(d => d.Distance).ThenBy(d => d.Slot).ToArray();
            if (donor.Length > 0) return (donor[0], gun);
        }
        return null;
    }
}
