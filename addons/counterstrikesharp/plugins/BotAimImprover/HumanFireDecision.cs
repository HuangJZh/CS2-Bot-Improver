#nullable enable
using System.Numerics;
namespace BotAimImprover;

public enum AimWeaponClass { Rifle, Pistol, PrecisePistol, Smg, Sniper, Shotgun, MachineGun, Other }
public readonly record struct HumanFireContext(AimWeaponClass Weapon= AimWeaponClass.Rifle,bool UnderFire=false)
{
    public static AimWeaponClass Classify(string? weapon)=>weapon switch
    {
        "weapon_awp" or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1"=>AimWeaponClass.Sniper,
        "weapon_deagle" or "weapon_revolver"=>AimWeaponClass.PrecisePistol,
        "weapon_glock" or "weapon_hkp2000" or "weapon_usp_silencer" or "weapon_p250" or "weapon_fiveseven"
            or "weapon_tec9" or "weapon_elite" or "weapon_cz75a"=>AimWeaponClass.Pistol,
        "weapon_mac10" or "weapon_mp9" or "weapon_mp7" or "weapon_mp5sd" or "weapon_ump45" or "weapon_p90" or "weapon_bizon"=>AimWeaponClass.Smg,
        "weapon_nova" or "weapon_xm1014" or "weapon_mag7" or "weapon_sawedoff"=>AimWeaponClass.Shotgun,
        "weapon_m249" or "weapon_negev"=>AimWeaponClass.MachineGun,
        "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_galilar" or "weapon_famas"
            or "weapon_aug" or "weapon_sg556"=>AimWeaponClass.Rifle,
        _=>AimWeaponClass.Other
    };
    // Approximate projectile speed in world units per second, used only for lead
    // planning. Values are gameplay hypotheses to calibrate, not measured ballistics.
    public static float BulletSpeedFor(AimWeaponClass weapon)=>weapon switch
    {
        AimWeaponClass.Sniper=>3500f,AimWeaponClass.PrecisePistol=>2600f,
        AimWeaponClass.Rifle=>3000f,AimWeaponClass.Smg=>2300f,
        AimWeaponClass.Pistol=>2100f,AimWeaponClass.Shotgun=>1500f,
        AimWeaponClass.MachineGun=>3000f,_=>2500f
    };
    public float BulletSpeed=>BulletSpeedFor(Weapon);
}
public readonly record struct FireDecision(bool Allow,string Reason,float? ExpectedErrorDegrees,float? ToleranceDegrees);

// Provisional speed/accuracy trade-off, not measured rank data or a hit predictor.
// Uses delayed beliefs and intended movement; never tests true target hitboxes.
public static class HumanFireDecision
{
    // Fire when the planned aim is within a plausible hit envelope of the delayed
    // target belief: target angular radius scaled by weapon class, reduced by
    // localization uncertainty, with a small floor so tiny distant targets are not
    // impossible. motorError is the profile's hand/precision scale and raises the
    // floor for less precise difficulty tiers.
    public static FireDecision Evaluate(Vector2 intendedAim,Vector2 estimatedBearing,float angularRadius,float uncertainty,
        bool visible,float observationAge,bool correctionPending,HumanFireContext context,float motorError=.16f)
    {
        if(!visible)return new(false,"not-visible",null,null);
        if(!float.IsFinite(observationAge) || observationAge<0 || observationAge>=.20f)return new(false,"stale-observation",null,null);
        if(!float.IsFinite(intendedAim.X+intendedAim.Y+estimatedBearing.X+estimatedBearing.Y+angularRadius+uncertainty+motorError)
            || angularRadius<=0 || uncertainty<0 || motorError<0)return new(false,"invalid-belief",null,null);
        if(correctionPending)return new(false,"waiting-for-correction",null,null);
        float weaponFactor=context.Weapon switch
        {
            AimWeaponClass.Sniper=>.40f,AimWeaponClass.PrecisePistol=>.55f,
            AimWeaponClass.Pistol=>1.0f,AimWeaponClass.Rifle=>.90f,AimWeaponClass.Smg=>1.15f,
            AimWeaponClass.Shotgun=>1.4f,AimWeaponClass.MachineGun=>1.0f,
            AimWeaponClass.Other=>.8f,_=>.9f
        };
        // Aim envelope: half the target's angular radius plus a human "body width"
        // allowance and the profile's precision scale, reduced by localization
        // uncertainty. The .45deg term keeps distant/small targets fireable at a
        // human-like (not pixel-perfect) tolerance; it is a calibration hypothesis.
        float effectiveRadius=MathF.Max(.02f,angularRadius-uncertainty*.35f);
        float baseTolerance=.5f*effectiveRadius+.45f+motorError*.5f;
        float tolerance=Math.Clamp(baseTolerance*weaponFactor*(context.UnderFire ? 1.3f : 1),.25f,4);
        float error=AimMath.Delta(estimatedBearing,intendedAim).Length();
        bool allow=error<=tolerance;
        return new(allow,allow ? "expected-alignment" : "waiting-for-alignment",error,tolerance);
    }
}
