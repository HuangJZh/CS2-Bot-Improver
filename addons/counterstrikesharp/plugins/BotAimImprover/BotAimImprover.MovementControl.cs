using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAimImprover;

// Movement-side helpers that do not replace native navigation:
//  * short strafe injections while map preaim is running (clear angles while moving);
//  * a fire escape override that walks a bot out of a molotov/inferno instead of
//    repeatedly stepping into the same flames.
public partial class BotAimImprover
{
    private sealed class StrafeState
    {
        public long Token;
        public float Until;
        public float Next;
        public bool Left;
    }
    private sealed class FireEscapeState
    {
        public long Token;
        public float Until;
    }
    private readonly Dictionary<int, StrafeState> _preaimStrafe = new();
    private readonly Dictionary<int, FireEscapeState> _fireEscape = new();
    private readonly List<Vector> _firePositions = new();
    private float _firePositionsAt = -10f;

    private void ResetPreAimStrafe(int slot)
    {
        if (_preaimStrafe.Remove(slot, out var state) && state.Token > 0)
        {
            try { _humanController?.CancelUsercmdInjection(slot, state.Token); } catch (Exception) { }
        }
    }
    private void ResetFireEscape(int slot)
    {
        if (_fireEscape.Remove(slot, out var state) && state.Token > 0)
        {
            try { _humanController?.CancelUsercmdMovement(slot, state.Token); } catch (Exception) { }
        }
    }
    private void ResetMovementControl()
    {
        foreach (int slot in _preaimStrafe.Keys.ToArray()) ResetPreAimStrafe(slot);
        foreach (int slot in _fireEscape.Keys.ToArray()) ResetFireEscape(slot);
    }

    // Small left/right steps while checking preaim angles. Only injected for a
    // quarter second at a time so native navigation still owns the route.
    private void TickPreAimStrafe(CCSPlayerController controller, float now)
    {
        int slot = controller.Slot;
        if (_preaimStrafe.TryGetValue(slot, out var active))
        {
            if (now < active.Until) return;
            ResetPreAimStrafe(slot);
        }
        if (_preaimStrafe.TryGetValue(slot, out active) && now < active.Next) return;
        bool left = Random.Shared.Next(2) == 0;
        long token = _humanController?.InjectUsercmd(slot,
            (ulong)(left ? PlayerButtons.Left : PlayerButtons.Right), 260) ?? -1;
        if (token <= 0) return;
        _preaimStrafe[slot] = new StrafeState
        {
            Token = token,
            Until = now + .26f,
            Next = now + 1.1f + Random.Shared.NextSingle() * .7f,
            Left = left,
        };
        WriteTelemetry(new
        {
            kind = "preaim_strafe",
            tick = Server.TickCount,
            time = now,
            slot,
            direction = left ? "left" : "right",
            duration_ms = 260,
        });
    }

    private static Vector? NearestFire(Vector origin, float radius, List<Vector> fires)
    {
        Vector? best = null;
        float bestSq = radius * radius;
        foreach (var fire in fires)
        {
            float dx = fire.X - origin.X, dy = fire.Y - origin.Y;
            float d2 = dx * dx + dy * dy;
            if (d2 < bestSq) { bestSq = d2; best = fire; }
        }
        return best;
    }

    private void RefreshFirePositions(float now)
    {
        if (now - _firePositionsAt < .25f) return;
        _firePositionsAt = now;
        _firePositions.Clear();
        try
        {
            foreach (var entity in Utilities.GetAllEntities())
            {
                if (entity == null || !entity.IsValid || entity.DesignerName != "inferno") continue;
                var baseEntity = entity.As<CBaseEntity>();
                if (baseEntity.AbsOrigin is { } origin) _firePositions.Add(origin);
            }
        }
        catch (Exception) { }
    }

    // If the bot is standing in/near a molotov, take over movement for a short
    // burst and walk directly away from the fire. Native AI resumes afterwards.
    private void TickFireAvoidance(CCSPlayerController controller, CCSPlayerPawn pawn, float now)
    {
        if (_humanController == null || !HumanActive || pawn.AbsOrigin is not { } origin)
        {
            ResetFireEscape(controller.Slot);
            return;
        }
        RefreshFirePositions(now);
        var fire = NearestFire(origin, 180f, _firePositions);
        if (fire == null)
        {
            ResetFireEscape(controller.Slot);
            return;
        }
        int slot = controller.Slot;
        if (_fireEscape.TryGetValue(slot, out var active))
        {
            if (now < active.Until) return;
            ResetFireEscape(slot);
        }
        float awayX = origin.X - fire.X;
        float awayY = origin.Y - fire.Y;
        float len = MathF.Sqrt(awayX * awayX + awayY * awayY);
        if (len < 1f) return;
        awayX /= len; awayY /= len;
        float yawRad = pawn.EyeAngles.Y * MathF.PI / 180f;
        float forwardX = MathF.Cos(yawRad), forwardY = MathF.Sin(yawRad);
        float leftX = -MathF.Sin(yawRad), leftY = MathF.Cos(yawRad);
        float forward = (awayX * forwardX + awayY * forwardY) * 450f;
        float left = (awayX * leftX + awayY * leftY) * 450f;
        long token = _humanController.StartUsercmdMovement(slot, forward, left);
        if (token <= 0) return;
        _fireEscape[slot] = new FireEscapeState { Token = token, Until = now + .55f };
        // Safety expiry: if the bot stops being processed by this plugin
        // (state released, other view owner, round edge), a persistent analog
        // override must still be cancelled instead of leaving the bot frozen.
        AddTimer(.65f, () =>
        {
            if (_fireEscape.TryGetValue(slot, out var activeEscape) && activeEscape.Token == token)
                ResetFireEscape(slot);
        });
        WriteTelemetry(new
        {
            kind = "fire_avoid",
            tick = Server.TickCount,
            time = now,
            slot,
            fire_x = fire.X,
            fire_y = fire.Y,
            forward,
            left,
            duration_ms = 550,
        });
    }
}
