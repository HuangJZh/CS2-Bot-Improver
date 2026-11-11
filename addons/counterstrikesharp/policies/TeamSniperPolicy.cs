namespace BotPurchasePolicy;

public readonly record struct SniperBuyer(uint Controller,int Slot,int Team,int Money,bool Eligible,bool HasPrimary,bool HasSniper);
public static class TeamSniperPolicy
{
    public static int Limit(int team)=>team switch{2=>1,3=>2,_=>0};
    public static bool IsSniper(string item)=>item is "weapon_awp" or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1";
    // Existing human/bot snipers consume slots. A carried primary is never
    // replaced to create a sniper, and rich unarmed bots reserve slots together.
    public static HashSet<uint> Assign(IReadOnlyList<SniperBuyer> players)
    {
        var result=new HashSet<uint>();
        foreach(int team in new[]{2,3})
        {
            int free=Math.Max(0,Limit(team)-players.Count(p=>p.Team==team&&p.HasSniper));
            foreach(var p in players.Where(p=>p.Team==team&&p.Eligible&&!p.HasPrimary&&p.Money>=4750)
                .OrderByDescending(p=>p.Money).ThenBy(p=>p.Slot).Take(free))result.Add(p.Controller);
        }
        return result;
    }
    public static string? RifleAlias(int team,int money,bool silenced=true)=>team switch
    {
        2 when money>=2700=>"ak47",
        2 when money>=1800=>"galilar",
        2 when money>=1050=>"mac10",
        3 when money>=2900=>silenced?"m4a1_silencer":"m4a1",
        3 when money>=1950=>"famas",
        3 when money>=1250=>"mp9",
        _=>null
    };
}
