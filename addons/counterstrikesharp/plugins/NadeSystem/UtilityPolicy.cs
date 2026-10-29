using System;

namespace NadeSystem;

// Pure policy shared by ordinary lineups, retaliation and objective utility.
// Counts are per round; a 5-bot "less" side can throw at most three grenades.
internal readonly record struct UtilityBudget(int PerBot, int PerTeam, float TeamInterval,
    int Flashes, int Explosives, double ExecutionChance);

internal static class UtilityPolicy
{
    internal static UtilityBudget ForMode(string mode, int bots)
    {
        bots = Math.Max(1, bots);
        return mode switch
        {
            "less" => new(1, Math.Max(1, (bots + 1) / 2), 4.5f, 1, 2, 0.40),
            "normal" => new(2, bots + 2, 3.0f, 2, 3, 0.60),
            "more" => new(3, bots * 2, 2.0f, 3, 5, 0.80),
            "max" => new(4, bots * 3, 1.25f, bots, bots * 2, 1.0),
            _ => new(0, 0, float.PositiveInfinity, 0, 0, 0.0),
        };
    }

    internal static bool Allows(UtilityBudget budget, string type, int botTotal,
        int teamTotal, int teamFlash, int teamExplosive, float now, float nextTeamThrow)
        => botTotal < budget.PerBot && teamTotal < budget.PerTeam && now >= nextTeamThrow
            && (type != "flash" || teamFlash < budget.Flashes)
            && (type is not ("he" or "molotov" or "incgrenade") || teamExplosive < budget.Explosives);

    internal static bool KnownPositionWithin(float x, float y, float z, float expires,
        float now, float landingX, float landingY, float landingZ, float range)
    {
        var dx = x - landingX; var dy = y - landingY; var dz = z - landingZ;
        return now <= expires && dx * dx + dy * dy + dz * dz <= range * range;
    }

    internal static float ApproximateSoundCoordinate(float position) => MathF.Round(position / 128f) * 128f;
}
