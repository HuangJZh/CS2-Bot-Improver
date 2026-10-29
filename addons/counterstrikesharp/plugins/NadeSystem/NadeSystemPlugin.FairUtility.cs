using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NadeSystem;

public partial class NadeSystemPlugin
{
    private readonly Dictionary<int, float> _nextTeamThrow = new();
    private readonly Dictionary<uint, int> _botUtilityTotal = new();
    private readonly Dictionary<int, int> _teamUtilityTotal = new();
    private uint _utilityRoundSerial;

    private UtilityBudget GetBudget(CCSPlayerController bot)
        => UtilityPolicy.ForMode(_botNadesMode, Utilities.GetPlayers()
            .Count(p => p.IsValid && p.IsBot && p.TeamNum == bot.TeamNum));

    private bool UtilityBudgetAllows(CCSPlayerController bot, string type)
    {
        var team = bot.TeamNum;
        _roundCountByTeam.TryGetValue(team, out var count);
        return UtilityPolicy.Allows(GetBudget(bot), type,
            _botUtilityTotal.GetValueOrDefault((uint)bot.Index),
            _teamUtilityTotal.GetValueOrDefault(team), count?.Flash ?? 0,
            (count?.HE ?? 0) + (count?.Molotov ?? 0), Server.CurrentTime,
            _nextTeamThrow.GetValueOrDefault(team));
    }

    private void CommitUtilityBudget(CCSPlayerController bot, string type)
    {
        uint index = (uint)bot.Index;
        _botUtilityTotal[index] = _botUtilityTotal.GetValueOrDefault(index) + 1;
        _teamUtilityTotal[bot.TeamNum] = _teamUtilityTotal.GetValueOrDefault(bot.TeamNum) + 1;
        _nextTeamThrow[bot.TeamNum] = Server.CurrentTime + GetBudget(bot).TeamInterval;
        IncrementCount(type == "incgrenade" ? "molotov" : type, bot.TeamNum);
        IncrementBotCount(type, index);
    }

    private void ResetFairUtility()
    {
        _utilityRoundSerial++;
        _nextTeamThrow.Clear();
        _botUtilityTotal.Clear();
        _teamUtilityTotal.Clear();
        _enemyKnowledge.Clear();
        _activeSmoke.Clear();
    }

    // Sound does not reveal the opponent's exact current head/feet coordinates.
    private readonly record struct EnemyKnowledge(float X, float Y, float Z,
        float ExpiresAt, bool HeardOnly);
    private readonly Dictionary<(uint Observer, uint Target), EnemyKnowledge> _enemyKnowledge = new();
    private readonly List<(float X, float Y, float Z, float ExpiresAt)> _activeSmoke = new();

    private static (uint, uint) KnowledgeKey(CCSPlayerController observer, CCSPlayerController target)
        => (observer.EntityHandle.Raw, target.EntityHandle.Raw);

    private void UpdateEnemyKnowledge(List<CCSPlayerController> players)
    {
        float now = Server.CurrentTime;
        foreach (var key in _enemyKnowledge.Where(k => k.Value.ExpiresAt < now).Select(k => k.Key).ToList())
            _enemyKnowledge.Remove(key);
        _activeSmoke.RemoveAll(s => s.ExpiresAt < now);
        foreach (var observer in players.Where(p => p.IsValid && p.IsBot && p.PawnIsAlive && !p.HasBeenControlledByPlayerThisRound))
        foreach (var target in players.Where(p => p.IsValid && p.PawnIsAlive && p.TeamNum is 2 or 3 && p.TeamNum != observer.TeamNum))
        {
            if (!EnemySeesTargetGeometric(observer, target)) continue;
            var origin = GetActiveLivePawn(target)?.AbsOrigin;
            if (origin != null)
                _enemyKnowledge[KnowledgeKey(observer, target)] = new(origin.X, origin.Y, origin.Z, now + 1.25f, false);
        }
    }

    private bool TryGetKnownEnemy(CCSPlayerController observer, CCSPlayerController target, out EnemyKnowledge known)
        => _enemyKnowledge.TryGetValue(KnowledgeKey(observer, target), out known)
            && Server.CurrentTime <= known.ExpiresAt;

    private bool KnownEnemyNear(CCSPlayerController observer, CCSPlayerController target, Vec3 landing, float range)
        => TryGetKnownEnemy(observer, target, out var known)
            && UtilityPolicy.KnownPositionWithin(known.X, known.Y, known.Z, known.ExpiresAt,
                Server.CurrentTime, landing.X, landing.Y, landing.Z, range);

    private bool SmokeBlocksSight(float x, float y, float z, float tx, float ty, float tz)
    {
        float dx = tx - x, dy = ty - y, dz = tz - z;
        float length = dx * dx + dy * dy + dz * dz;
        if (length < 1f) return false;
        foreach (var smoke in _activeSmoke)
        {
            float t = Math.Clamp(((smoke.X - x) * dx + (smoke.Y - y) * dy + (smoke.Z + 60f - z) * dz) / length, 0f, 1f);
            float sx = x + t * dx - smoke.X, sy = y + t * dy - smoke.Y, sz = z + t * dz - smoke.Z - 60f;
            if (sx * sx + sy * sy + sz * sz < 170f * 170f) return true;
        }
        return false;
    }

    // Check friendly positions only for safety; enemy decisions use knowledge snapshots.
    private bool FriendlyFlashSafe(CCSPlayerController bot, Vec3 landing, List<CCSPlayerController> players)
        => !players.Any(p =>
        {
            if (!p.IsValid || p.TeamNum != bot.TeamNum) return false;
            var pos = GetActiveLivePawn(p)?.AbsOrigin;
            return pos != null && Dist3D(pos.X, pos.Y, pos.Z + 64f, landing.X, landing.Y, landing.Z) < 1000f
                && FlashHasLoS(landing, pos.X, pos.Y, pos.Z + 64f);
        });
}
