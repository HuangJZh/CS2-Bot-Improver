namespace BotAimImprover;

public static class CombatBalancePolicy
{
    // A small parameter handicap, never a damage multiplier or a claimed win-rate.
    public static HumanAimProfile AllyProfile(HumanAimProfile p,float strength)=>p with
    {
        Reaction=p.Reaction/strength,SensorDelay=p.SensorDelay/strength,
        FlickTime=p.FlickTime/strength,TrackingTime=p.TrackingTime/strength,
        FlickAmplitudeTime=p.FlickAmplitudeTime/strength,FirstShotSettleLimit=p.FirstShotSettleLimit/strength,
        MaxSpeed=p.MaxSpeed*strength,MotorError=p.MotorError/strength,
        AmplitudeError=p.AmplitudeError/strength,PerceptionError=p.PerceptionError/strength,
        BurstPause=p.BurstPause/strength
    };
    public static bool IsHumanAlly(int team,int humanTeams)=>team is 2 or 3
        &&humanTeams==(1<<team); // Spectators and humans on both teams get no asymmetric handicap.
    public static float HeadIntentChance(string difficulty,AimWeaponClass weapon,string? item)
    {
        if(item is "weapon_awp" or "weapon_scar20" or "weapon_g3sg1"||weapon==AimWeaponClass.Shotgun)return 0;
        float chance=difficulty switch {"Low"=>.30f,"High"=>.50f,_=>.40f};
        return Math.Clamp(chance+(weapon switch {AimWeaponClass.PrecisePistol=>.15f,
            AimWeaponClass.Smg or AimWeaponClass.MachineGun=>-.10f,_=>0f}),0,1);
    }
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
