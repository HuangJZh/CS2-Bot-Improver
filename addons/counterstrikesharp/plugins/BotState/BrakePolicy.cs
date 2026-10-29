namespace BotState;
internal static class BrakePolicy
{
    internal static double Chance(string difficulty) => difficulty.ToLowerInvariant() switch { "low" => .60, "high" => .95, _ => .80 };
    internal static bool NeedsStop(string weapon, float distance) => weapon switch
    {
        "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug" or "weapon_sg556" or "weapon_famas" or "weapon_galilar" or "weapon_awp" or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1" or "weapon_deagle" or "weapon_revolver" => true,
        "weapon_tec9" or "weapon_glock" or "weapon_p250" or "weapon_fiveseven" or "weapon_cz75a" or "weapon_elite" => distance > 550,
        "weapon_hkp2000" or "weapon_usp_silencer" => distance > 250,
        "weapon_mac10" or "weapon_mp9" or "weapon_mp7" or "weapon_mp5sd" or "weapon_ump45" or "weapon_p90" or "weapon_bizon" => distance > 800,
        _ => false
    };
}
