using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAimImprover;

// Movement-side helper that does not replace native navigation: short left/right
// strafe injections while map preaim is running (clear angles while moving).
// Fire avoidance via StartUsercmdMovement was removed: it is a persistent analog
// override and could leave a released bot movement-locked.
public partial class BotAimImprover
{
    private sealed class StrafeState
    {
        public long Token;
        public float Until;
        public float Next;
        public bool Left;
    }
    private readonly Dictionary<int, StrafeState> _preaimStrafe = new();

    private void ResetPreAimStrafe(int slot)
    {
        if (_preaimStrafe.Remove(slot, out var state) && state.Token > 0)
        {
            try { _humanController?.CancelUsercmdInjection(slot, state.Token); } catch (Exception) { }
        }
    }
    private void ResetMovementControl()
    {
        foreach (int slot in _preaimStrafe.Keys.ToArray()) ResetPreAimStrafe(slot);
    }

    // Small left/right steps while checking preaim angles. Only injected for a
    // quarter second at a time so native navigation still owns the route.
    private void TickPreAimStrafe(CCSPlayerController controller, float now)
    {
        int slot = controller.Slot;
        if (_preaimStrafe.TryGetValue(slot, out var active))
        {
            if (now < active.Until) return;
            if (now < active.Next) return;
            ResetPreAimStrafe(slot);
        }
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
}
