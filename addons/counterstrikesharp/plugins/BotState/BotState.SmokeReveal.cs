using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace BotState;

public partial class BotState
{
    // Damage provides a direction to the victim, never a shared live target reveal.
    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo _)
    {
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        if (victim is { IsValid: true, IsBot: true } && attacker is { IsValid: true }
            && victim.Slot != attacker.Slot)
            RegisterRearResponse(victim, attacker);
        return HookResult.Continue;
    }

    // Clear any reveal inherited from an older plugin before a hot reload.
    private static void ClearReveals() => Server.ExecuteCommand("bv_reveal clear");
}
