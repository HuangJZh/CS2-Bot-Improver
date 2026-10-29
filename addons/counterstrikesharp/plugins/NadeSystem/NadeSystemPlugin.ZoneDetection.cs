using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NadeSystem;

public partial class NadeSystemPlugin : BasePlugin
{
    private void CheckBotZones()
    {
        if (_botNadesMode == "off" || _roundOver || _freezeEndTime <= 0f || _mapNades.Count == 0) return;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault();
        if (rules?.GameRules?.FreezePeriod == true) return;
        var players = Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").ToList();
        if (_tick % 8 == 0) UpdateEnemyKnowledge(players);
        foreach (var bot in players)
        {
            if (!bot.IsValid || !bot.IsBot || !bot.PawnIsAlive || bot.HasBeenControlledByPlayerThisRound
                || _replayBots.Contains((uint)bot.Index)) continue;
            var pawn = GetActiveLivePawn(bot);
            var pos = pawn?.AbsOrigin;
            if (pawn?.Bot == null || pos == null || !HasLiveEnemyForTeam(bot.TeamNum, players)) continue;
            if (!_grenadeZoneGrid.TryGetValue(GetGrenadeZoneGridCell(pos.X, pos.Y), out var nearby)) continue;
            foreach (var g in nearby)
            {
                float dx = pos.X - g.ZoneX, dy = pos.Y - g.ZoneY;
                float dz = pos.Z + 64f - g.ProjectilePosition.Z;
                float radius = g.GrenadeType == "decoy" ? 200f : g.ZoneRadius;
                if (dx * dx + dy * dy > radius * radius || MathF.Abs(dz) > 85f
                    || IsOnCooldown(g.Id) || IsOnProbFailCooldown(g.Id)
                    || !FacesThrowDirection(pawn, g) || !UtilityBudgetAllows(bot, g.GrenadeType)) continue;
                // Decoy window-breaking replays obey the same team spacing and
                // total limits, and cannot bypass low preset budgets.
                if (g.GrenadeType == "decoy")
                {
                    CommitUtilityBudget(bot, "decoy");
                    RegisterCooldown(g.Id, "decoy");
                    SpawnProjectile(bot, g);
                }
                else TryConditionalReplay(bot, g, players);
                break;
            }
        }
    }

    private static bool HasLiveEnemyForTeam(int team, List<CCSPlayerController> players)
        => players.Any(p => p.IsValid && p.PawnIsAlive && p.TeamNum is 2 or 3 && p.TeamNum != team);

    private bool FacesThrowDirection(CCSPlayerPawn pawn, GrenadeData g)
    {
        var angles = pawn.EyeAngles;
        if (angles == null) return false;
        float yaw = angles.Y * MathF.PI / 180f;
        float length = MathF.Sqrt(g.ProjectileVelocity.X * g.ProjectileVelocity.X + g.ProjectileVelocity.Y * g.ProjectileVelocity.Y);
        if (length <= 0f) return true;
        return MathF.Cos(yaw) * g.ProjectileVelocity.X / length + MathF.Sin(yaw) * g.ProjectileVelocity.Y / length >= 0f;
    }

    private CCSPlayerPawn? GetActiveLivePawn(CCSPlayerController player)
    {
        if (!player.IsValid) return null;
        var pawn = player.Pawn?.Value;
        if (pawn == null || !pawn.IsValid || pawn.LifeState != 0 || pawn.Health <= 0) return null;
        return pawn.As<CCSPlayerPawn>();
    }
}
