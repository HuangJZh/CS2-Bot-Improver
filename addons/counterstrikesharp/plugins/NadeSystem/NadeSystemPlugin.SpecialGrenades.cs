using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Events;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.UserMessages;
using CounterStrikeSharp.API.Modules.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NadeSystem;

public partial class NadeSystemPlugin : BasePlugin
{
    // ═══════════════════════════════════════════════════════════
    //  Special Nades
    // ═══════════════════════════════════════════════════════════
    // Defuse smoke/flash
    // * Spawns and charges an immediate situational grenade
    private void TrySpawnInstantGrenade(CCSPlayerController bot, Vector spawnPos, string gtype, Vector? velocity = null)
    {
        if (_botNadesMode == "off") return;
        if (_roundOver || !UtilityBudgetAllows(bot, gtype)) return;
        if (gtype == "flash" && !FriendlyFlashSafe(bot, new Vec3 { X = spawnPos.X, Y = spawnPos.Y, Z = spawnPos.Z }, Utilities.GetPlayers())) return;
        // In case the bot has been taken over
        bool isTakenOver = bot.HasBeenControlledByPlayerThisRound;
        if (isTakenOver) return;
        bool hasLiveEnemy = Utilities
            .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Any(p => p.IsValid && p.PawnIsAlive
                && ((int)p.TeamNum == 2 || (int)p.TeamNum == 3)
                && (int)p.TeamNum != bot.TeamNum);
        if (!hasLiveEnemy) return;

        var money = bot.InGameMoneyServices;
        if (money == null) return;

        bool isCT     = bot.TeamNum == (int)CsTeam.CounterTerrorist;
        var costTable = isCT ? CostCT : CostT;
        if (!costTable.TryGetValue(gtype, out int cost)) return;
        if (money.Account < cost) return;

        uint botIdx  = (uint)bot.Index;
        if (!HasLockedNadeMoney(botIdx, cost)) return;
        bool isPoor   = _poorBots.Contains((uint)bot.Index);
        int  spendCap = GetRoundSpendCap(isCT, isPoor);
        if (!_roundSpendPerBot.TryGetValue(botIdx, out int alreadySpent))
            alreadySpent = 0;
        if (alreadySpent + cost > spendCap) return;
        bool deduct = true;

        if (deduct)
        {
            money.Account -= cost;
            Utilities.SetStateChanged(bot, "CCSPlayerController", "m_pInGameMoneyServices");
            _roundSpendPerBot[botIdx] = alreadySpent + cost;
        }
        SpendLockedNadeMoney(botIdx, cost);
        CommitUtilityBudget(bot, gtype);

        var vel = velocity ?? new Vector(0f, 0f, 0f);
        uint roundSerial = _utilityRoundSerial;
        Server.NextFrame(() =>
        {
            if (_roundOver || _botNadesMode == "off" || roundSerial != _utilityRoundSerial) return;
            try
            {
                var botPawn = bot.PlayerPawn?.Value;
                if (botPawn == null || !botPawn.IsValid) return;

                int teamNum = bot.TeamNum;

                if (gtype == "smoke")
                {
                    var smoke = _smokeCreate.Invoke(
                        spawnPos.Handle, spawnPos.Handle,
                        vel.Handle, vel.Handle,
                        botPawn.Handle, 45, teamNum);
                    if (smoke == null || !smoke.IsValid) return;
                    smoke.TeamNum             = (byte)teamNum;
                    smoke.Thrower.Raw         = botPawn.EntityHandle.Raw;
                    smoke.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
                    smoke.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
                    AnnounceGrenadeThrow(bot, gtype);
                }
                else if (gtype == "flash")
                {
                    var flash = Utilities.CreateEntityByName<CFlashbangProjectile>(
                        "flashbang_projectile");
                    if (flash == null) return;
                    flash.TeamNum             = (byte)teamNum;
                    flash.Thrower.Raw         = botPawn.EntityHandle.Raw;
                    flash.OriginalThrower.Raw = botPawn.EntityHandle.Raw;
                    flash.OwnerEntity.Raw     = botPawn.EntityHandle.Raw;
                    flash.InitialPosition.X   = spawnPos.X;
                    flash.InitialPosition.Y   = spawnPos.Y;
                    flash.InitialPosition.Z   = spawnPos.Z;
                    flash.InitialVelocity.X   = vel.X;
                    flash.InitialVelocity.Y   = vel.Y;
                    flash.InitialVelocity.Z   = vel.Z;
                    flash.Elasticity          = 0.33f;
                    var ang = new QAngle(-90f, 0f, 0f);
                    flash.Teleport(spawnPos, ang, vel);
                    flash.DispatchSpawn();
                    flash.Teleport(spawnPos, ang, vel);
                    AnnounceGrenadeThrow(bot, gtype);
                }
            }
            catch (Exception ex)
            {
                Server.PrintToConsole($"[NadeSystem] TrySpawnInstantGrenade error: {ex.Message}");
            }
        });
    }

    // * Triggers defensive smoke or flash support during a defuse
    private HookResult OnBombBeginDefuse(EventBombBegindefuse @event, GameEventInfo info)
    {
        var bot = @event.Userid;
        if (bot == null || !bot.IsValid || !bot.IsBot) return HookResult.Continue;
        if (bot.HasBeenControlledByPlayerThisRound) return HookResult.Continue;

        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !bot.PawnIsAlive)
            return HookResult.Continue;

        var pos = pawn.AbsOrigin;
        if (pos == null) return HookResult.Continue;
        var spawnPos = new Vector(pos.X, pos.Y, pos.Z + 5f);

        // Defuse smoke
        if (!_defuseSmokeUsed)
        {
            bool hasDefuser = false;
            if (pawn.ItemServices != null
                && pawn.ItemServices.Handle != nint.Zero)
            {
                hasDefuser = new CCSPlayer_ItemServices(pawn.ItemServices.Handle).HasDefuser;
            }

            if (hasDefuser || Random.Shared.NextDouble() < 0.33)
            {
                _defuseSmokeUsed = true;
                TrySpawnInstantGrenade(bot, spawnPos, "smoke");
            }
        }

        // Defuse flash
        if (!_defuseFlashUsed)
        {
            if (Random.Shared.NextDouble() < 0.20)
            {
                _defuseFlashUsed = true;
                // Don't flash yourself
                // No artificial self-flash immunity.
                var flashVel = new Vector(0f, 0f, -800f);
                TrySpawnInstantGrenade(bot, spawnPos, "flash", flashVel);
            }
        }

        return HookResult.Continue;
    }

    // Plant smoke
    // * Triggers smoke support when a bot starts planting
    private HookResult OnBombBeginPlant(EventBombBeginplant @event, GameEventInfo info)
    {
        if (_plantSmokeUsed) return HookResult.Continue;

        var bot = @event.Userid;
        if (bot == null || !bot.IsValid || !bot.IsBot) return HookResult.Continue;
        if (bot.HasBeenControlledByPlayerThisRound) return HookResult.Continue;

        if (Random.Shared.NextDouble() >= 0.33) return HookResult.Continue;

        var pawn = bot.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !bot.PawnIsAlive)
            return HookResult.Continue;

        var pos = pawn.AbsOrigin;
        if (pos == null) return HookResult.Continue;

        _plantSmokeUsed = true;
        TrySpawnInstantGrenade(bot, new Vector(pos.X, pos.Y, pos.Z + 5f), "smoke");
        return HookResult.Continue;
    }

    // * Dispatches damage-driven grenade reactions
    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo info)
    {
        HandleMolotovEscape(@event);
        HandleRetaliationHE(@event);

        return HookResult.Continue;
    }
    // Put out the fire
    // * Uses smoke to help a bot escape sustained fire damage
    private void HandleMolotovEscape(EventPlayerHurt @event)
    {
        if (_botNadesMode == "off") return;
        var victim = @event.Userid;
        if (victim == null || !victim.IsValid || !victim.IsBot) return;
        if (victim.HasBeenControlledByPlayerThisRound) return;

        var pawn = victim.PlayerPawn?.Value;
        if (pawn == null || !pawn.IsValid || !victim.PawnIsAlive) return;

        string weapon = @event.Weapon ?? "";
        bool isMolotovDmg = weapon.Contains("inferno", StringComparison.OrdinalIgnoreCase)
                         || weapon.Contains("molotov", StringComparison.OrdinalIgnoreCase)
                         || weapon.Contains("incgrenade", StringComparison.OrdinalIgnoreCase);
        if (!isMolotovDmg)
        {
            _botMolotovDmgStart.Remove((uint)victim.Index);
            return;
        }

        int teamNum = victim.TeamNum;
        if (_molotovEscapeSmokeCooldown.TryGetValue(teamNum, out float expiry)
            && Server.CurrentTime < expiry) return;

        uint idx = (uint)victim.Index;
        float now = Server.CurrentTime;

        if (!_botMolotovDmgStart.TryGetValue(idx, out float start))
        {
            _botMolotovDmgStart[idx] = now;
            return;
        }

        if (now - start < 0.3f) return;

        _botMolotovDmgStart.Remove(idx);
        _molotovEscapeSmokeCooldown[teamNum] = now + 20f;

        var pos = pawn.AbsOrigin;
        if (pos == null) return;
        TrySpawnInstantGrenade(victim, new Vector(pos.X, pos.Y, pos.Z + 5f), "smoke");
    }
    // Revenge grenade
    // * Selects and throws a retaliatory explosive grenade
    private void HandleRetaliationHE(EventPlayerHurt @event)
    {
        if (_botNadesMode == "off" || _roundOver) return;
        var victim = @event.Userid;
        var attacker = @event.Attacker;
        if (victim == null || attacker == null || !victim.IsValid || !victim.IsBot
            || victim.HasBeenControlledByPlayerThisRound || !victim.PawnIsAlive
            || attacker.TeamNum == victim.TeamNum) return;
        string weapon = @event.Weapon ?? "";
        if (!weapon.Contains("hegrenade", StringComparison.OrdinalIgnoreCase)
            && !weapon.Contains("inferno", StringComparison.OrdinalIgnoreCase)
            && !weapon.Contains("molotov", StringComparison.OrdinalIgnoreCase)
            && !weapon.Contains("incgrenade", StringComparison.OrdinalIgnoreCase)) return;
        if (_retaliationCooldown.TryGetValue(victim.TeamNum, out float until) && Server.CurrentTime < until) return;
        // Being hit does not reveal a thrower's current location through walls.
        if (!TryGetKnownEnemy(victim, attacker, out _)) return;
        _retaliationCooldown[victim.TeamNum] = Server.CurrentTime + 8f;
        uint roundSerial = _utilityRoundSerial;
        AddTimer(0.7f + (float)Random.Shared.NextDouble() * 0.6f, () =>
        {
            if (!victim.IsValid || !victim.PawnIsAlive || _roundOver || roundSerial != _utilityRoundSerial) return;
            var pawn = GetActiveLivePawn(victim);
            var pos = pawn?.AbsOrigin;
            if (pawn == null || pos == null) return;
            var players = Utilities.GetPlayers();
            UpdateEnemyKnowledge(players);
            // Retaliation uses a real nearby release point and a remembered
            // position, never an arbitrary lineup beside a hidden attacker.
            foreach (var g in _mapNades.Where(g => g.GrenadeType is "he" or "molotov" or "incgrenade"))
            {
                if (IsOnCooldown(g.Id) || !KnownEnemyNear(victim, attacker, g.LandingPosition, 240f)
                    || !FacesThrowDirection(pawn, g)
                    || Dist3D(pos.X, pos.Y, pos.Z + 64f, g.ProjectilePosition.X, g.ProjectilePosition.Y, g.ProjectilePosition.Z) > 110f) continue;
                TryConditionalReplay(victim, g, players);
                break;
            }
        }, TimerFlags.STOP_ON_MAPCHANGE);
    }
}
