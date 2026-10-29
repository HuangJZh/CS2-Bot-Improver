namespace BotAI;

// Keep crash/navigation repairs, but retain the engine's human-like combat gates.
// The platform tables remain available for comparing future upstream updates.
internal static class CombatPatchPolicy
{
    private static readonly HashSet<string> Disabled = new(StringComparer.Ordinal)
    {
        "AttackState_SkipFireRateCheck", "SprayAllDistances_ForceHoldTrigger",
        "AttackState_SkipSteadyFireShortcut", "AttackState_SkipZoomFireShortcut",
        "AttackState_SkipSniperSpreadCheck", "AttackState_DodgeChance100_Always",
        "AttackState_RetreatOnSniper_Disable", "AttackState_CanStrafe_jne",
        "AllSkill_KeepMoving_WhenSeeSniper", "SniperDodge_SkipIsSniper_DodgeA",
        "SniperCrouchDodge_jb", "Vision_AlwaysWatchApproachPoints",
        "Vision_ApproachBody_SkipSkillCheck", "Vision_ApproachBody_SkipHidingSpotCheck",
        "Vision_SkipIsMovingGate", "Vision_AlwaysEnterApproachBody",
        "IsNoticable_AlwaysTrue", "Upkeep_BotCOS_ZeroDrift", "Upkeep_BotSIN_ZeroDrift",
        "InvestigateNoise_SkipSelfDefenseCheck", "OnAudibleEvent_GlobalHearRange",
        "BombPickup_CT_GlobalHearRange", "BombBeep_CT_GlobalHearRange",
        "OnBombPlanted_AllBotsLearnSite", "FlashbangAvoidance_Disable"
    };

    internal static bool IsAllowed(string name) =>
        !Disabled.Contains(name.EndsWith("_Cave", StringComparison.Ordinal) ? name[..^5] : name);
}
