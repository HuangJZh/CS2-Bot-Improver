using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NadeSystem;

public partial class NadeSystemPlugin : BasePlugin
{
    private void TryConditionalReplay(CCSPlayerController bot, GrenadeData g,
        List<CCSPlayerController> players, FlashTargetEvaluation? flashEvaluation = null)
    {
        var pawn = GetActiveLivePawn(bot);
        if (pawn == null || !UtilityBudgetAllows(bot, g.GrenadeType)) return;
        if (!PassesSituationalCheck(bot, pawn, g, g.GrenadeType, players, flashEvaluation)) return;
        // A player does not know/execute every lineup encountered. A failed
        // attempt is throttled, instead of re-rolling every four server ticks.
        if (Random.Shared.NextDouble() > GetBudget(bot).ExecutionChance)
        {
            RegisterProbFailCooldown(g.Id);
            return;
        }
        TryReplay(bot, g, players);
    }

    private bool PassesSituationalCheck(CCSPlayerController bot, CCSPlayerPawn pawn,
        GrenadeData g, string type, List<CCSPlayerController> players,
        FlashTargetEvaluation? flashEvaluation)
    {
        if (!PassesTeamAndScheduleCheck(bot, g)) return false;
        if (type is "he" or "molotov" or "incgrenade")
        {
            if (FiredRecently(bot, 1f)) return false;
            // Use the throwing bot's own last-known location, not the actual
            // current position of an opponent behind a wall or smoke.
            if (!players.Any(p => p.IsValid && p.TeamNum != bot.TeamNum
                && KnownEnemyNear(bot, p, g.LandingPosition, 240f))) return false;
            if (type is "molotov" or "incgrenade" && _cooldowns.Any(cd =>
            {
                if (cd.ExpiresAt <= Server.CurrentTime) return false;
                var smoke = FindMapGrenade(cd.GrenadeId, "smoke");
                return smoke != null && Dist3D(g.LandingPosition.X, g.LandingPosition.Y, g.LandingPosition.Z,
                    smoke.LandingPosition.X, smoke.LandingPosition.Y, smoke.LandingPosition.Z) < 200f;
            })) return false;
        }
        if (type == "flash")
        {
            if (!FriendlyFlashSafe(bot, g.LandingPosition, players)) return false;
            if ((flashEvaluation ?? EvaluateFlashTargets(bot, g, players)).BlindableEnemies.Count == 0) return false;
        }
        if (type == "smoke")
        {
            if (_cooldowns.Any(cd =>
            {
                if (cd.ExpiresAt <= Server.CurrentTime) return false;
                var smoke = FindMapGrenade(cd.GrenadeId, "smoke");
                return smoke != null && Dist3D(g.LandingPosition.X, g.LandingPosition.Y, g.LandingPosition.Z,
                    smoke.LandingPosition.X, smoke.LandingPosition.Y, smoke.LandingPosition.Z) < 250f;
            })) return false;
            // Tagged opening smokes are planned map control; no enemy location
            // is queried. Untagged reactive smokes require real information.
            if (string.IsNullOrEmpty(g.TeamTag) && !players.Any(p => p.IsValid && p.TeamNum != bot.TeamNum
                && KnownEnemyNear(bot, p, g.LandingPosition, 2200f))) return false;
        }
        return true;
    }

    private bool PassesTeamAndScheduleCheck(CCSPlayerController bot, GrenadeData g)
    {
        if (string.IsNullOrEmpty(g.TeamTag)) return true;
        if (g.TeamTag != (bot.TeamNum == 3 ? "CT" : "T")) return false;
        string key = $"{Server.MapName.ToLowerInvariant()}_{g.TeamTag}";
        float window = ThrowSchedule.TryGetValue(key, out var seconds) ? seconds : 15f;
        return _freezeEndTime > 0f && Server.CurrentTime - _freezeEndTime <= window;
    }

    // This is an estimate using remembered positions. The bot cannot inspect
    // an unseen opponent's live view angles to calculate a guaranteed flash.
    private FlashTargetEvaluation EvaluateFlashTargets(CCSPlayerController bot,
        GrenadeData g, List<CCSPlayerController> players)
    {
        var candidates = new List<CCSPlayerController>();
        int knownCount = 0;
        foreach (var p in players)
        {
            if (!p.IsValid || p.TeamNum == bot.TeamNum || !TryGetKnownEnemy(bot, p, out var known)) continue;
            knownCount++;
            if (UtilityPolicy.KnownPositionWithin(known.X, known.Y, known.Z, known.ExpiresAt,
                Server.CurrentTime, g.LandingPosition.X, g.LandingPosition.Y, g.LandingPosition.Z, 1300f)
                && FlashHasLoS(g.LandingPosition, known.X, known.Y, known.Z + 64f)) candidates.Add(p);
        }
        return new FlashTargetEvaluation(candidates, knownCount);
    }

    private bool FlashHasLoS(Vec3 startPoint, float x, float y, float z)
    {
        try
        {
            var start = new Vector(startPoint.X, startPoint.Y, startPoint.Z);
            var end = new Vector(x, y, z);
            var options = new TraceOptions { InteractsWith = Masks.SolidBrushOnly };
            return Trace.TraceEndShape(start, end, options: options).Fraction >= 0.99f;
        }
        catch
        {
            // A missing or broken ray-trace service must not grant vision.
            return false;
        }
    }
}
