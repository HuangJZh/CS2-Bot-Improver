using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BotAimImprover;

// "Silent contact" layer: bots sometimes walk (and occasionally crouch-walk)
// while clearing, especially in clutch/grouped pushes, so they do not announce
// their position with running footsteps. The Walk/Duck buttons are injected via
// BotController for a bounded window; any visible enemy cancels the silent
// movement immediately.
public partial class BotAimImprover
{
    private sealed class SilentState
    {
        public long WalkToken;
        public long DuckToken;
        public float Until;
        public string Mode = "";
        public string Reason = "";
    }
    private readonly Dictionary<int, SilentState> _silentStates = new();
    private readonly Dictionary<int, float> _silentNextDecision = new();

    private void ResetSilentMovement(int slot)
    {
        if (_silentStates.Remove(slot, out var state))
        {
            try
            {
                if (state.WalkToken > 0) _humanController?.CancelUsercmdInjection(slot, state.WalkToken);
                if (state.DuckToken > 0) _humanController?.CancelUsercmdInjection(slot, state.DuckToken);
            }
            catch (Exception) { }
        }
    }
    private void ResetAllSilentMovement()
    {
        foreach (var slot in _silentStates.Keys.ToArray()) ResetSilentMovement(slot);
        _silentNextDecision.Clear();
    }
    private void TickSilentMovement(CCSPlayerController controller, CCSPlayerPawn pawn, CCSBot bot, float now)
    {
        if (!HumanActive || bot.IsEnemyVisible)
        {
            ResetSilentMovement(controller.Slot);
            return;
        }
        if (_silentStates.TryGetValue(controller.Slot, out var active) && now < active.Until) return;
        ResetSilentMovement(controller.Slot);
        if (_silentNextDecision.TryGetValue(controller.Slot, out float next) && now < next) return;
        var origin = pawn.AbsOrigin;
        if (origin == null) return;
        var teammates = EntityPlayers()
            .Where(p => p is { IsValid: true, PawnIsAlive: true } && p.TeamNum == controller.TeamNum && p.Slot != controller.Slot)
            .ToList();
        int alive = teammates.Count + 1;
        bool nearbyTeammate = false;
        foreach (var mate in teammates)
        {
            var mateOrigin = mate.PlayerPawn.Value?.AbsOrigin;
            if (mateOrigin == null) continue;
            float dx = mateOrigin.X - origin.X, dy = mateOrigin.Y - origin.Y;
            if (dx * dx + dy * dy < 700f * 700f) { nearbyTeammate = true; break; }
        }
        float probability;
        float minDuration, maxDuration;
        string reason;
        if (alive <= 2)
        {
            probability = .28f; minDuration = 0.6f; maxDuration = 3.0f;
            reason = "clutch-silent-search";
        }
        else if (nearbyTeammate)
        {
            probability = .06f; minDuration = 0.5f; maxDuration = 2.5f;
            reason = "group-silent-push";
        }
        else
        {
            probability = .06f; minDuration = 0.5f; maxDuration = 2.0f;
            reason = "control-silent-check";
        }
        if (Random.Shared.NextSingle() >= probability)
        {
            _silentNextDecision[controller.Slot] = now + 1.6f;
            return;
        }
        bool crouch = (alive <= 2 && Random.Shared.NextSingle() < .15f)
            || (reason == "group-silent-push" && Random.Shared.NextSingle() < .08f);
        int durationMs = (int)((minDuration + Random.Shared.NextSingle() * (maxDuration - minDuration)) * 1000f);
        long walkToken = _humanController?.InjectUsercmd(controller.Slot, (ulong)PlayerButtons.Walk, durationMs) ?? -1;
        long duckToken = crouch
            ? _humanController?.InjectUsercmd(controller.Slot, (ulong)PlayerButtons.Duck, durationMs) ?? -1
            : -1;
        if (walkToken <= 0)
        {
            _silentNextDecision[controller.Slot] = now + 2f;
            return;
        }
        string mode = crouch ? "walk+crouch" : "walk";
        _silentStates[controller.Slot] = new SilentState
        {
            WalkToken = walkToken,
            DuckToken = duckToken,
            Until = now + durationMs / 1000f,
            Mode = mode,
            Reason = reason,
        };
        WriteTelemetry(new
        {
            kind = "silent_movement",
            tick = Server.TickCount,
            time = now,
            slot = controller.Slot,
            mode,
            reason,
            duration_ms = durationMs,
            alive_team = alive,
            nearby_teammate = nearbyTeammate,
        });
    }
}
