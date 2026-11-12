namespace BotAimImprover;

public static class CombatBalancePolicy
{
    public const float DefaultAllyStrength = .75f;
    public const float DefaultEnemyStrength = .80f;
    public static float NormalizeEnemyStrength(float strength)=>float.IsFinite(strength)?Math.Clamp(strength,.25f,2f):DefaultEnemyStrength;

    public static float NormalizeAllyStrength(float strength)
    {
        return float.IsFinite(strength) ? Math.Clamp(strength, DefaultAllyStrength, 1f) : DefaultAllyStrength;
    }

    // A combat-parameter scale, never a damage multiplier or a claimed win-rate.
    public static HumanAimProfile AllyProfile(HumanAimProfile p,float strength)=>p.ScaleCombat(strength);
    public static bool IsHumanAlly(int team,int humanTeams)=>team is 2 or 3
        &&humanTeams==(1<<team); // Spectators and humans on both teams get no asymmetric handicap.
    public static bool IsHumanEnemy(int team,int humanTeams)=>team is 2 or 3
        &&humanTeams==(1<<(team==2?3:2));
    public const float MediumEnemyReactionSeconds=.170f;
    public static HumanAimProfile EnemyProfile(HumanAimProfile p,float strength,string? difficulty=null)
    {
        var profile=p.ScaleCombat(NormalizeEnemyStrength(strength));
        // User tuning applies to ordinary Medium enemies after global scaling.
        // Elite scaling is applied by the caller afterwards; allies stay intact.
        return difficulty=="Medium"?profile with{Reaction=MediumEnemyReactionSeconds}:profile;
    }
    public static float HeadIntentChance(string difficulty,AimWeaponClass weapon,string? item)
    {
        if(item is "weapon_awp" or "weapon_scar20" or "weapon_g3sg1"||weapon==AimWeaponClass.Shotgun)return 0;
        float chance=difficulty switch {"Low"=>.30f,"High"=>.50f,_=>.40f};
        return Math.Clamp(chance+(weapon switch {AimWeaponClass.PrecisePistol=>.15f,
            AimWeaponClass.Smg or AimWeaponClass.MachineGun=>-.10f,_=>0f}),0,1);
    }
}

public readonly record struct BalanceBot(uint Controller,int Team);
public sealed class EnemyEliteRoster
{
    private readonly Dictionary<uint,float> _strengths=new();
    private int _humanTeams;
    private bool _selected;
    public IReadOnlyDictionary<uint,float> Strengths=>_strengths;
    public bool Refresh(int humanTeams,IEnumerable<BalanceBot> bots,Random random)
    {
        if(humanTeams!=_humanTeams){Reset();_humanTeams=humanTeams;}
        if(_selected||humanTeams is not (4 or 8))return false;
        int enemy=humanTeams==4?3:2;
        var candidates=bots.Where(b=>b.Team==enemy).Select(b=>b.Controller).Distinct().ToArray();
        if(candidates.Length==0)return false;
        int count=random.Next(1,Math.Min(2,candidates.Length)+1);
        for(int i=0;i<count;i++)
        {
            int j=random.Next(i,candidates.Length);(candidates[i],candidates[j])=(candidates[j],candidates[i]);
            _strengths[candidates[i]]=1.20f+random.NextSingle()*.10f;
        }
        _selected=true;return true;
    }
    public float Strength(uint controller)=>_strengths.GetValueOrDefault(controller,1f);
    public void Reset(){_strengths.Clear();_selected=false;_humanTeams=0;}
}

public readonly record struct AimIntentKey(uint Pawn,uint Target,uint Weapon,int Mode);
public sealed class EngagementAimIntents
{
    private readonly Dictionary<int,(AimIntentKey Key,float Chance,bool Head,float Seen)> _plans=new();
    public bool Resolve(int slot,AimIntentKey key,float chance,float now,Func<float> draw)
    {
        if(_plans.TryGetValue(slot,out var p)&&p.Key==key&&p.Chance==chance&&now>=p.Seen&&now-p.Seen<=1)
        {_plans[slot]=(key,chance,p.Head,now);return p.Head;}
        bool head=chance>=1||chance>0&&draw()<chance;
        _plans[slot]=(key,chance,head,now);return head;
    }
    public bool? Get(int slot)=>_plans.TryGetValue(slot,out var p)?p.Head:null;
    public void Clear()=>_plans.Clear();
}
