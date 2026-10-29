namespace BotState;
internal static class BrakePolicy
{
    internal static double Chance(string difficulty) => difficulty.ToLowerInvariant() switch { "low" => .60, "high" => .95, _ => .80 };
    // Approximate map scale: 80-120 Source units is the requested 2-3 m band.
    internal static double StopPreference(string weapon, float distance)
    {
        if (!float.IsFinite(distance) || distance < 0) return 0;
        bool mayRunAtContact = weapon is
            "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug" or "weapon_sg556" or "weapon_famas" or "weapon_galilar"
            or "weapon_hkp2000" or "weapon_usp_silencer" or "weapon_tec9" or "weapon_glock" or "weapon_p250" or "weapon_fiveseven" or "weapon_cz75a" or "weapon_elite"
            or "weapon_mac10" or "weapon_mp9" or "weapon_mp7" or "weapon_mp5sd" or "weapon_ump45" or "weapon_p90" or "weapon_bizon";
        if (mayRunAtContact) return Math.Clamp((distance-80f)/40f,0,1);
        // Precision pistols and all non-run-and-gun gun classes retain stop intent even at contact.
        return weapon is "weapon_awp" or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1"
            or "weapon_deagle" or "weapon_revolver" or "weapon_nova" or "weapon_mag7" or "weapon_sawedoff" or "weapon_xm1014"
            or "weapon_negev" or "weapon_m249" ? 1 : 0;
    }
    internal static bool NeedsStop(string weapon,float distance) => StopPreference(weapon,distance)>0;
}
