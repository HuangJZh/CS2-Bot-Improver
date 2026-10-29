using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;
using Microsoft.Extensions.Logging;

namespace BotState;

internal static class HumanCombatSettings
{
    public static readonly FakeConVar<string> Difficulty = new(
        "bot_human_difficulty", "Human reaction preset: low, medium, high. Apply with matching profile files.", "medium");
}

public partial class BotState
{
    private sealed record RearResponse(uint Pawn, float Until, long AttackToken, bool OwnAimLock);
    private readonly Dictionary<int, RearResponse> _rearResponses = new();

    // Extra time to orient after a hit outside the screen. Ordinary visible-target
    // acquisition is governed by botprofile/BT, so it is not delayed a second time.
    private void RegisterRearResponse(CCSPlayerController victim, CCSPlayerController attacker)
    {
        if (victim.HasBeenControlledByPlayerThisRound || attacker.TeamNum == victim.TeamNum
            || victim.PlayerPawn.Value is not { IsValid: true } pawn
            || attacker.PlayerPawn.Value?.AbsOrigin is not { } source || pawn.AbsOrigin is not { } origin)
            return;
        float sourceYaw = MathF.Atan2(source.Y - origin.Y, source.X - origin.X) * 180f / MathF.PI;
        if (!HumanReactionPolicy.OutsideScreen(pawn.EyeAngles.Y, sourceYaw)) return;
        int slot = victim.Slot;
        float now = Server.CurrentTime;
        // A burst cannot continuously extend the same orientation delay.
        if (_rearResponses.TryGetValue(slot, out var current) && current.Pawn == pawn.EntityHandle.Raw && now < current.Until)
            return;
        ReleaseRearResponse(slot);
        var range = HumanReactionPolicy.RearDelay(HumanCombatSettings.Difficulty.Value);
        float delay = range.Min + (float)_random.NextDouble() * (range.Max - range.Min);
        long attackToken = 0;
        bool ownAimLock = false;
        try
        {
            if (_botController != null && !BotControllerBridge.IsViewControlled(_botController, slot))
            {
                attackToken = BotControllerBridge.StartUsercmdSuppression(_botController, slot, (ulong)PlayerButtons.Attack);
                ownAimLock = BotControllerBridge.LockAim(_botController, slot);
            }
        }
        catch (Exception error) { Logger.LogWarning(error, "[HumanCombat] Rear response uses native surprise timer; controller unavailable."); }
        // Retain any partially acquired tokens so the expiry path always releases them.
        _rearResponses[slot] = new(pawn.EntityHandle.Raw, now + delay, attackToken, ownAimLock);
        if (pawn.Bot is { } bot)
        {
            bot.SurpriseTimer.Duration = delay;
            bot.SurpriseTimer.Timestamp = Math.Max(bot.SurpriseTimer.Timestamp, now + delay);
            bot.SurpriseTimer.Timescale = 1;
        }
    }

    private void ExpireRearResponses(float now)
    {
        foreach (var (slot, response) in _rearResponses.ToArray())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (now >= response.Until || player?.IsValid != true || !player.IsBot || !player.PawnIsAlive
                || player.HasBeenControlledByPlayerThisRound || player.PlayerPawn.Value?.EntityHandle.Raw != response.Pawn)
                ReleaseRearResponse(slot);
        }
    }

    private void ReleaseRearResponse(int slot)
    {
        if (!_rearResponses.Remove(slot, out var response) || _botController == null) return;
        try
        {
            if (response.AttackToken > 0)
                BotControllerBridge.CancelUsercmdSuppression(_botController, slot, response.AttackToken);
        }
        catch (Exception error) { Logger.LogWarning(error, "[HumanCombat] Could not release attack suppression for slot {Slot}.", slot); }
        try { if (response.OwnAimLock) BotControllerBridge.UnlockAim(_botController, slot); }
        catch (Exception error) { Logger.LogWarning(error, "[HumanCombat] Could not release aim lock for slot {Slot}.", slot); }
    }

    private void ReleaseHumanReactionControls()
    {
        foreach (var slot in _rearResponses.Keys.ToArray()) ReleaseRearResponse(slot);
    }
}
