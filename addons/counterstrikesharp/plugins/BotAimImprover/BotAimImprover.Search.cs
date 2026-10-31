using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vec = System.Numerics.Vector3;

namespace BotAimImprover;

// Team search coordination: clustered preaim nodes are partitioned among the
// alive bots of a team (nearest-node ownership), so two bots naturally split
// left/right and five bots each cover a small area. Bots sweep their own nodes
// approximately (coarse clusters, short dwell) and "forget" a difficulty-scaled
// random subset of nodes for the round, like a human missing a spot.
public partial class BotAimImprover
{
    private sealed class SearchNode
    {
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("z")] public float Z { get; set; }
        [JsonPropertyName("sources")] public int Sources { get; set; }
    }
    private sealed class SearchMap
    {
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("nodes")] public List<SearchNode> Nodes { get; set; } = new();
    }
    private sealed class SearchFile
    {
        [JsonPropertyName("maps")] public Dictionary<string, SearchMap> Maps { get; set; } = new();
    }
    private readonly Dictionary<string, List<SearchNode>> _searchNodesByMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, Dictionary<int, int>> _searchOwnersByTeam = new();
    private readonly Dictionary<int, float> _searchAssignAt = new();
    private readonly Dictionary<int, int> _searchAssignAlive = new();
    private readonly Dictionary<int, Dictionary<int, float>> _searchCooldown = new();
    private readonly Dictionary<int, HashSet<int>> _searchForgotten = new();
    private int? _searchForgottenRound;
    private string? _searchLoadedMap;
    private bool _searchLoadAttempted;

    private void LoadSearchNodes()
    {
        if (_searchLoadAttempted) return;
        _searchLoadAttempted = true;
        try
        {
            var path = Path.Combine(ModuleDirectory, "preaim-nodes.json");
            if (!File.Exists(path)) return;
            var file = JsonSerializer.Deserialize<SearchFile>(File.ReadAllText(path));
            if (file == null) return;
            foreach (var (map, data) in file.Maps)
                if (data.Nodes.Count > 0) _searchNodesByMap[map] = data.Nodes;
            Logger.LogInformation("[BotAimImprover] Loaded search nodes for {Maps} maps ({Nodes} nodes)",
                _searchNodesByMap.Count, _searchNodesByMap.Values.Sum(n => n.Count));
        }
        catch (Exception error)
        {
            Logger.LogWarning(error, "Search nodes unavailable; map preaim falls back to raw points");
        }
    }
    private List<SearchNode>? CurrentSearchNodes()
    {
        LoadSearchNodes();
        var map = Server.MapName;
        if (string.IsNullOrEmpty(map)) return null;
        if (!string.Equals(_searchLoadedMap, map, StringComparison.OrdinalIgnoreCase))
        {
            _searchLoadedMap = map;
            _searchOwnersByTeam.Clear();
            _searchAssignAt.Clear();
            _searchAssignAlive.Clear();
            _searchCooldown.Clear();
            _searchForgotten.Clear();
            _searchForgottenRound = null;
        }
        return _searchNodesByMap.TryGetValue(map, out var nodes) ? nodes : null;
    }
    private int CurrentRoundForgetKey()
    {
        try { return _telemetryRules?.TotalRoundsPlayed ?? 0; } catch { return 0; }
    }
    private void EnsureSearchOwners(int team, List<SearchNode> nodes, float now)
    {
        var alive = Utilities.GetPlayers()
            .Where(p => p is { IsValid: true, IsBot: true, PawnIsAlive: true } && p.TeamNum == team)
            .ToList();
        int aliveCount = alive.Count;
        if (aliveCount == 0) return;
        if (_searchOwnersByTeam.ContainsKey(team)
            && _searchAssignAlive.TryGetValue(team, out int lastAlive) && lastAlive == aliveCount
            && now < _searchAssignAt.GetValueOrDefault(team) + 2.5f)
            return;
        var positions = new List<(int Slot, float X, float Y)>();
        foreach (var player in alive)
        {
            var pawn = player.PlayerPawn.Value;
            var origin = pawn?.AbsOrigin;
            if (origin == null) continue;
            positions.Add((player.Slot, origin.X, origin.Y));
        }
        if (positions.Count == 0) return;
        var owners = new Dictionary<int, int>();
        var counts = new Dictionary<int, int>();
        foreach (var p in positions) counts[p.Slot] = 0;
        // Greedy nearest-node assignment with a per-bot quota: every alive bot gets
        // a small area, so a 2-man team splits left/right and a 5-man team each
        // covers a compact zone instead of some bots owning nothing.
        int quota = Math.Max(3, (nodes.Count + positions.Count - 1) / positions.Count);
        var pairs = new List<(float Dist, int Node, int Slot)>();
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            foreach (var p in positions)
            {
                float dx = node.X - p.X, dy = node.Y - p.Y;
                pairs.Add((dx * dx + dy * dy, i, p.Slot));
            }
        }
        pairs.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        var assigned = new bool[nodes.Count];
        foreach (var pair in pairs)
        {
            if (assigned[pair.Node]) continue;
            if (counts[pair.Slot] >= quota) continue;
            assigned[pair.Node] = true;
            owners[pair.Node] = pair.Slot;
            counts[pair.Slot]++;
        }
        // Leftover nodes (or bots skipped by quota): nearest bot regardless.
        for (int i = 0; i < nodes.Count; i++)
        {
            if (assigned[i]) continue;
            int bestSlot = positions[0].Slot;
            float bestDist = float.MaxValue;
            foreach (var p in positions)
            {
                float dx = nodes[i].X - p.X, dy = nodes[i].Y - p.Y;
                float d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; bestSlot = p.Slot; }
            }
            assigned[i] = true;
            owners[i] = bestSlot;
            counts[bestSlot]++;
        }
        // Guarantee every bot owns at least one node: steal the nearest node from
        // the largest owner when a bot ended up empty.
        foreach (var empty in positions.Where(p => counts[p.Slot] == 0).ToList())
        {
            int donorSlot = counts.OrderByDescending(kv => kv.Value).First().Key;
            int bestNode = -1;
            float bestDist = float.MaxValue;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (!owners.TryGetValue(i, out int owner) || owner != donorSlot) continue;
                float dx = nodes[i].X - empty.X, dy = nodes[i].Y - empty.Y;
                float d = dx * dx + dy * dy;
                if (d < bestDist) { bestDist = d; bestNode = i; }
            }
            if (bestNode >= 0)
            {
                owners[bestNode] = empty.Slot;
                counts[donorSlot]--;
                counts[empty.Slot]++;
            }
        }
        _searchOwnersByTeam[team] = owners;
        _searchAssignAt[team] = now;
        _searchAssignAlive[team] = aliveCount;
        var logCounts = positions.Select(p => $"{p.Slot}:{counts[p.Slot]}").ToArray();
        Logger.LogInformation("[BotAimImprover] Search assign team={Team} alive={Alive} owned={Owned}", team, aliveCount, string.Join(" ", logCounts));
    }    // Pick an owned, not-forgotten, not-recently-checked search node. Ownership
    // makes bots split the map by area; forgetting skips some nodes entirely for
    // the round; cooldown stops a bot from staring at the same node repeatedly.
    private bool TrySelectSearchNode(CCSPlayerController controller, Vec botPos, float currentYaw, float now,
        List<SearchNode> nodes, out int nodeIndex, out Vec point)
    {
        nodeIndex = -1; point = default;
        EnsureSearchOwners(controller.TeamNum, nodes, now);
        if (!_searchOwnersByTeam.TryGetValue(controller.TeamNum, out var owners)) return false;
        if (!_searchCooldown.TryGetValue(controller.Slot, out var cooldowns))
        {
            cooldowns = new Dictionary<int, float>();
            _searchCooldown[controller.Slot] = cooldowns;
        }
        var forgotten = GetForgottenSet(controller.Slot, nodes.Count);
        float bestScore = float.MaxValue;
        int bestIndex = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (owners.TryGetValue(i, out int owner) && owner != controller.Slot) continue;
            if (forgotten.Contains(i)) continue;
            if (cooldowns.TryGetValue(i, out float until) && now < until) continue;
            var node = nodes[i];
            float dx = node.X - botPos.X, dy = node.Y - botPos.Y, dz = node.Z - botPos.Z;
            float distance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            if (distance < 150f || distance > 3600f) continue;
            float targetYaw = MathF.Atan2(dy, dx) * 180f / MathF.PI;
            float yawDelta = MathF.Abs(((targetYaw - currentYaw + 540f) % 360f) - 180f);
            if (yawDelta > 100f) continue;
            float score = distance + yawDelta * 16f;
            if (score < bestScore) { bestScore = score; bestIndex = i; }
        }
        if (bestIndex < 0) return false;
        var chosen = nodes[bestIndex];
        point = new Vec(chosen.X, chosen.Y, chosen.Z);
        nodeIndex = bestIndex;
        cooldowns[bestIndex] = now + 6f;
        return true;
    }
    private HashSet<int> GetForgottenSet(int slot, int totalNodes)
    {
        int round = CurrentRoundForgetKey();
        if (_searchForgottenRound != round)
        {
            _searchForgotten.Clear();
            _searchForgottenRound = round;
        }
        if (!_searchForgotten.TryGetValue(slot, out var set))
        {
            float probability = _humanDifficulty switch
            {
                "Low" => .30f,
                "High" => .12f,
                _ => .20f
            };
            set = new HashSet<int>();
            for (int i = 0; i < totalNodes; i++)
                if (Random.Shared.NextSingle() < probability) set.Add(i);
            // Never forget the entire list; the bot still checks something.
            if (totalNodes > 0 && set.Count >= totalNodes) set.Remove(0);
            _searchForgotten[slot] = set;
        }
        return set;
    }    // Used by the telemetry layer to expose per-bot search ownership counts.
    private (int Owned, int Alive, int Forgotten) SearchDebug(CCSPlayerController controller)
    {
        var nodes = CurrentSearchNodes();
        if (nodes == null) return (0, 0, 0);
        EnsureSearchOwners(controller.TeamNum, nodes, Server.CurrentTime);
        int owned = 0;
        if (_searchOwnersByTeam.TryGetValue(controller.TeamNum, out var owners))
            owned = owners.Count(kv => kv.Value == controller.Slot);
        int alive = Utilities.GetPlayers().Count(p => p is { IsValid: true, IsBot: true, PawnIsAlive: true } && p.TeamNum == controller.TeamNum);
        int forgotten = _searchForgotten.TryGetValue(controller.Slot, out var set) ? set.Count : 0;
        return (owned, alive, forgotten);
    }
}
