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
using Angles = System.Numerics.Vector2;

namespace BotAimImprover;

public partial class BotAimImprover
{
    private sealed class PreAimPoint
    {
        [JsonPropertyName("x")] public float X { get; set; }
        [JsonPropertyName("y")] public float Y { get; set; }
        [JsonPropertyName("z")] public float Z { get; set; }
        [JsonPropertyName("yaw")] public float Yaw { get; set; }
        [JsonPropertyName("route")] public string Route { get; set; } = "";
    }
    private sealed class PreAimMap
    {
        [JsonPropertyName("count")] public int Count { get; set; }
        [JsonPropertyName("points")] public List<PreAimPoint> Points { get; set; } = new();
    }
    private sealed class PreAimFile
    {
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("maps")] public Dictionary<string, PreAimMap> Maps { get; set; } = new();
    }
    private sealed class PreAimTarget
    {
        public Vec Point;
        public float Until;
        public float NextScan;
        public int NodeIndex = -1;
    }
    private readonly Dictionary<string, List<PreAimPoint>> _preaimByMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, PreAimTarget> _preaimTargets = new();
    private string? _preaimLoadedMap;
    private bool _preaimLoadAttempted;

    private void LoadPreAimData()
    {
        if (_preaimLoadAttempted) return;
        _preaimLoadAttempted = true;
        try
        {
            var path = Path.Combine(ModuleDirectory, "preaim-points.json");
            if (!File.Exists(path)) return;
            var file = JsonSerializer.Deserialize<PreAimFile>(File.ReadAllText(path));
            if (file == null) return;
            foreach (var (map, data) in file.Maps)
                if (data.Points.Count > 0) _preaimByMap[map] = data.Points;
            Logger.LogInformation("[BotAimImprover] Loaded preaim points for {Maps} maps ({Points} points)",
                _preaimByMap.Count, _preaimByMap.Values.Sum(p => p.Count));
        }
        catch (Exception error)
        {
            Logger.LogWarning(error, "Preaim points unavailable; map preaim disabled");
        }
    }
    private List<PreAimPoint>? CurrentPreAimPoints()
    {
        LoadPreAimData();
        var map = Server.MapName;
        if (string.IsNullOrEmpty(map)) return null;
        if (!string.Equals(_preaimLoadedMap, map, StringComparison.OrdinalIgnoreCase))
        {
            _preaimLoadedMap = map;
            _preaimTargets.Clear();
        }
        return _preaimByMap.TryGetValue(map, out var points) ? points : null;
    }
    // While a bot has no valid enemy it checks map-specific prefire angles: the
    // workshop prefire maps mark where an enemy may hold, so sweep the nearest
    // in-front marker with the same bounded turn used for sound preaim.
    private bool TryApplyMapPreaim(CCSPlayerController controller, CCSBot bot, nint pointer, CCSPlayerPawn pawn, Vec eyeVec, float now)
    {
        var points = CurrentPreAimPoints();
        if (points == null || points.Count == 0) return false;
        var origin = pawn.AbsOrigin;
        if (origin == null) return false;
        var botPos = new Vec(origin.X, origin.Y, origin.Z);
        bool hasTarget = _preaimTargets.TryGetValue(controller.Slot, out var target) && target != null;
        if (!hasTarget || now > target!.Until)
        {
            if (hasTarget && now < target!.NextScan) return false;
            float currentYaw = pawn.EyeAngles.Y;
            Vec chosen = default;
            int nodeIndex = -1;
            bool found = false;
            var searchNodes = CurrentSearchNodes();
            if (searchNodes is { Count: > 0 })
                found = TrySelectSearchNode(controller, botPos, currentYaw, now, searchNodes, out nodeIndex, out chosen);
            if (!found)
            {
                // Fallback when the clustered node file is unavailable: use the raw
                // aim_target points with the same distance/FOV filter.
                PreAimPoint? best = null;
                float bestScore = float.MaxValue;
                foreach (var p in points)
                {
                    float dx = p.X - botPos.X, dy = p.Y - botPos.Y;
                    float distance = MathF.Sqrt(dx * dx + dy * dy);
                    if (distance < 120f || distance > 3200f) continue;
                    float targetYaw = MathF.Atan2(dy, dx) * 180f / MathF.PI;
                    float yawDelta = MathF.Abs(((targetYaw - currentYaw + 540f) % 360f) - 180f);
                    if (yawDelta > 100f) continue;
                    float score = distance + yawDelta * 18f;
                    if (score < bestScore) { bestScore = score; best = p; }
                }
                if (best != null)
                {
                    chosen = new Vec(best.X, best.Y, best.Z);
                    found = true;
                }
            }
            if (!found)
            {
                _preaimTargets[controller.Slot] = new PreAimTarget { NextScan = now + .5f };
                return false;
            }
            target = new PreAimTarget
            {
                Point = chosen,
                NodeIndex = nodeIndex,
                Until = now + 1.1f,
                NextScan = now + 1.1f,
            };
            _preaimTargets[controller.Slot] = target;
        }
        var desired = AimMath.Angles(eyeVec, target.Point);
        var current = new Angles(pawn.EyeAngles.X, pawn.EyeAngles.Y);
        var delta = AimMath.Delta(desired, current);
        var step = new Angles(Math.Clamp(delta.X, -10f, 10f), Math.Clamp(delta.Y, -16f, 16f));
        var spot = AimMath.Ray(eyeVec, AimMath.Normalize(current + step));
        _modelSpots[pointer] = spot;
        WriteModelSpot(bot, spot);
        RefreshFireGuard(controller);
        return true;
    }
}
