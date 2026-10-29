using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Cvars;

namespace BotState;
internal static class BrakeSettings
{
    public static readonly FakeConVar<bool> Enabled = new("bot_fake_brake", "Probabilistic artificial braking before firing", true);
}
public partial class BotState
{
    private sealed class BrakeAttempt
    {
        public uint Pawn, Enemy;
        public float LastSeen, StopAt, ReleaseAt;
        public long Token;
        public bool Successful, Attempted;
        public double IntentRoll;
    }
    private readonly Dictionary<int, BrakeAttempt> _brakes = new();
    private void ReleaseBrake(int slot, bool remove)
    {
        if (!_brakes.TryGetValue(slot, out var state)) return;
        if (state.Token > 0 && _botController != null)
            BotControllerBridge.CancelUsercmdSuppression(_botController, slot, state.Token);
        state.Token = 0;
        if (remove) _brakes.Remove(slot);
    }
    private void ClearBrakeControls()
    {
        foreach (var slot in _brakes.Keys.ToArray()) ReleaseBrake(slot, true);
    }
    private void ExpireBrakeControls(float now)
    {
        foreach (var (slot,state) in _brakes.ToArray())
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (!BrakeSettings.Enabled.Value || player?.IsValid != true || !player.IsBot || !player.PawnIsAlive
                || player.HasBeenControlledByPlayerThisRound || player.PlayerPawn.Raw != state.Pawn || now-state.LastSeen > 1)
                ReleaseBrake(slot,true);
            else if (state.Token > 0 && now >= state.ReleaseAt) ReleaseBrake(slot,false);
        }
    }
    private void UpdateBrake(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, float now)
    {
        if (!BrakeSettings.Enabled.Value || _botController == null || !player.PawnIsAlive
            || player.HasBeenControlledByPlayerThisRound || pawn.MoveType == MoveType_t.MOVETYPE_LADDER
            || !pawn.GroundEntity.IsValid || pawn.IsDefusing || _rearResponses.ContainsKey(player.Slot)) return;
        var enemy = bot.Enemy.Value;
        if (!bot.IsEnemyVisible || enemy?.IsValid != true || enemy.AbsOrigin == null || pawn.AbsOrigin == null) return;
        var weapon = pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName;
        if (weapon == null) return;
        var dx=enemy.AbsOrigin.X-pawn.AbsOrigin.X; var dy=enemy.AbsOrigin.Y-pawn.AbsOrigin.Y; var dz=enemy.AbsOrigin.Z-pawn.AbsOrigin.Z;
        double preference=BrakePolicy.StopPreference(weapon,MathF.Sqrt(dx*dx+dy*dy+dz*dz));
        if (preference <= 0) { ReleaseBrake(player.Slot,false); return; }
        int slot=player.Slot;
        if (!_brakes.TryGetValue(slot,out var state) || state.Enemy != enemy.EntityHandle.Raw || state.Pawn != player.PlayerPawn.Raw)
        {
            state = new() { Pawn=player.PlayerPawn.Raw, Enemy=enemy.EntityHandle.Raw, LastSeen=now,
                IntentRoll=_random.NextDouble(),
                Successful=_random.NextDouble() < BrakePolicy.Chance(HumanCombatSettings.Difficulty.Value) };
            _brakes[slot]=state;
        }
        state.LastSeen=now;
        // Reevaluate distance with the same random threshold, never reroll every tick.
        // A retreating target can make a previously running bot decide to stop.
        if (!state.Attempted && state.IntentRoll < preference)
        {
            state.Attempted=true;
            if (state.Successful && pawn.AbsVelocity.Length2D()>20)
            {
                state.StopAt=now+.025f+(float)_random.NextDouble()*.035f;
                state.ReleaseAt=now+.12f;
                state.Token=BotControllerBridge.StartUsercmdSuppression(_botController,slot,(ulong)PlayerButtons.Attack);
            }
            else state.Successful=false;
        }
        if (!state.Attempted) return;
        if (!state.Successful || now >= state.ReleaseAt) return;
        // Tick preparation, not weapon_fire: do not retrospectively change a fired bullet.
        // Preserve any longer native reaction/weapon cadence gate.
        bot.FireWeaponTimestamp=Math.Max(bot.FireWeaponTimestamp,state.ReleaseAt);
        if (now >= state.StopAt)
        {
            pawn.AbsVelocity.X=0; pawn.AbsVelocity.Y=0;
        }
    }
}
