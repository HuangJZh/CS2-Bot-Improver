namespace BotSupport;

public readonly record struct Gun(string Item, string Label, int Price);
public readonly record struct Donor(int Slot, int Money, double Distance);
public static class SupportPolicy
{
    public static int StopChance(string difficulty) => difficulty switch { "Low" => 75, "High" => 95, _ => 85 };
    public static bool StopSucceeds(string difficulty, int roll) => roll >= 0 && roll < StopChance(difficulty);
    public static bool IsWeaponRequest(string token) => token.Trim().ToLowerInvariant() is
        "cw.needweapon" or "cw.needrop" or "cw.needdrop" or "#chatwheel_requestweapon" or "#chatwheel_needrop" or "needrop";
    public static Gun[] Guns(int team, string request)
    {
        if(team is not (2 or 3)) return [];
        Gun[] rifles = team == 2
            ? [new("weapon_ak47", "AK-47", 2700), new("weapon_galilar", "Galil AR", 1800)]
            : [new("weapon_m4a1_silencer", "M4A1-S", 2900), new("weapon_m4a1", "M4A4", 2900), new("weapon_famas", "FAMAS", 1950)];
        Gun[] smgs = [new("weapon_p90", "P90", 2350), new("weapon_bizon", "PP-Bizon", 1300),
            new("weapon_mp7", "MP7", 1400), new("weapon_mp5sd", "MP5-SD", 1400), new("weapon_ump45", "UMP-45", 1200),
            team == 2 ? new("weapon_mac10", "MAC-10", 1050) : new("weapon_mp9", "MP9", 1250)];
        Gun? preferred = request.ToLowerInvariant() switch
        {
            "auto" or "awp" or "awm" => new("weapon_awp", "AWP", 4750),
            "ak" or "ak47" when team == 2 => new("weapon_ak47", "AK-47", 2700),
            "m4a1" or "m4a1_silencer" when team == 3 => new("weapon_m4a1_silencer", "M4A1-S", 2900),
            "m4a4" when team == 3 => new("weapon_m4a1", "M4A4", 2900),
            _ => null
        };
        if(preferred == null) return [];
        return new[] { preferred.Value }.Concat(rifles.OrderByDescending(g => g.Price))
            .Concat(smgs.OrderByDescending(g => g.Price)).DistinctBy(g => g.Item).ToArray();
    }
    public static (Donor Donor, Gun Gun)? Select(IEnumerable<Donor> donors, int team, string request, int? choice = null)
    {
        var candidates = donors.Where(d => double.IsFinite(d.Distance) && d.Distance <= 1500)
            .OrderByDescending(d => d.Money).ThenBy(d => d.Distance).ThenBy(d => d.Slot).ToArray();
        if(candidates.Length == 0) return null;
        var donor=candidates[0];
        var guns=Guns(team,request);
        if(request.Equals("auto",StringComparison.OrdinalIgnoreCase))
        {
            var main=guns.Where(g => g.Item is "weapon_awp" or "weapon_ak47" or "weapon_m4a1_silencer" or "weapon_m4a1")
                .Where(g => g.Price <= donor.Money).ToArray();
            if(main.Length > 0)
                return (donor,main[choice.HasValue ? (int)((uint)choice.Value % (uint)main.Length) : Random.Shared.Next(main.Length)]);
        }
        foreach(var gun in guns)
            if(donor.Money >= gun.Price) return (donor,gun);
        return null;
    }
}
