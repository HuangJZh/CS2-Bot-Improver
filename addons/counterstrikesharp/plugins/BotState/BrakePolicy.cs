namespace BotState;
internal static class BrakePolicy
{
    internal static double Chance(string difficulty) => difficulty.ToLowerInvariant() switch { "low" => .60, "high" => .95, _ => .80 };
    // Distance-dependent intent is separate from the chance of executing a chosen stop.
    internal static double StopPreference(string weapon, float distance)
    {
        if (!float.IsFinite(distance) || distance < 0) return 0;
        (float near,float far) = weapon switch
        {
            "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug" or "weapon_sg556" or "weapon_famas" or "weapon_galilar" => (180,700),
            "weapon_awp" or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1" => (80,250),
            "weapon_deagle" or "weapon_revolver" => (150,500),
            "weapon_hkp2000" or "weapon_usp_silencer" => (200,600),
            "weapon_tec9" or "weapon_glock" or "weapon_p250" or "weapon_fiveseven" or "weapon_cz75a" or "weapon_elite" => (300,900),
            "weapon_mac10" or "weapon_mp9" or "weapon_mp7" or "weapon_mp5sd" or "weapon_ump45" or "weapon_p90" or "weapon_bizon" => (450,1200),
            "weapon_nova" or "weapon_mag7" or "weapon_sawedoff" or "weapon_xm1014" => (150,650),
            "weapon_negev" or "weapon_m249" => (200,900),
            _ => (float.PositiveInfinity,float.PositiveInfinity)
        };
        if (!float.IsFinite(near)) return 0;
        return Math.Clamp((distance-near)/(far-near),0,1);
    }
    internal static bool NeedsStop(string weapon,float distance) => StopPreference(weapon,distance)>0;
}
