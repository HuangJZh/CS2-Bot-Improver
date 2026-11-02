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
    //  Load
    // ═══════════════════════════════════════════════════════════

    // * Initializes data, event handlers, listeners, and commands
    public override void Load(bool hotReload)
    {
        Directory.CreateDirectory(DataDir);
        LoadDb();
        InitializeNadeLearning();

        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterListener<Listeners.OnTick>(OnTick);
        RegisterEventHandler<EventBombBegindefuse>(OnBombBeginDefuse);
        RegisterEventHandler<EventBombBeginplant>(OnBombBeginPlant);
        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerBlind>(OnPlayerBlind);
        RegisterEventHandler<EventWeaponFire>(OnWeaponFire);
        RegisterEventHandler<EventPlayerSound>(OnPlayerSound);
        RegisterListener<Listeners.OnMapStart>(_ =>
        {
            _inventoryRound++;
            _nadeLearning.Clear();
            _db.Clear();
            LoadDb();
            _cooldowns.Clear();
            _roundCountByTeam.Clear();
            _replayBots.Clear();
            _roundNadeMoneyPerBot.Clear();
        });

        AddCommand("bot_nades", "Control bots' nade throw mode (off/less/normal/more/max)", CmdBotNades);

        Server.PrintToConsole($"[NadeSystem] Loaded — {_db.Count} grenades in DB.");
    }


    // * Resets all per-round plugin state when a round starts
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        _inventoryRound++;
        var anchor=Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
            .Where(p=>p.IsValid&&p.TeamNum is 2 or 3).OrderBy(p=>p.Index).FirstOrDefault();
        _nadeLearning.BeginRound(anchor?.EntityHandle.Raw??0,anchor?.TeamNum??0);
        _nadeLearningExplore=Random.Shared.NextDouble()<.20;_nadeLearningPriorityLogged=false;
        _roundOver  = false;
        _freezeEndTime = 0f;
        _roundStartTime = Server.CurrentTime;
        _lastBuyAttempt = 0f;
        _roundCountByTeam.Clear();
        _roundCountByBot.Clear();
        _cooldowns.Clear();
        _replayBots.Clear();
        _smokeCooldownBots.Clear();
        _roundSpendPerBot.Clear();
        _roundNadeMoneyPerBot.Clear();
        _defuseSmokeUsed  = false;
        _defuseFlashUsed  = false;
        _plantSmokeUsed   = false;
        _botMolotovDmgStart.Clear();
        _earlySmokeCountByTeam.Clear();
        _botInFlashZone.Clear();
        _botFlashRatioWindow.Clear();
        _botFlashImmunityUntil.Clear();
        _molotovEscapeSmokeCooldown.Clear();
        _retaliationCooldown.Clear();
        // Information System
        _playerSounds.Clear();
        _botLastFireTime.Clear();
        foreach (var key in _probFailCooldown.Where(kv => kv.Value <= Server.CurrentTime).Select(kv => kv.Key).ToList())
            _probFailCooldown.Remove(key);
        // Save money for poor bots
        _poorBots.Clear();
        if (!IsPistolRound())
        {
            foreach (var bot in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
            {
                if (!bot.IsValid || !bot.IsBot) continue;
                // Mark bots with < 2800 as poor
                if (bot.InGameMoneyServices?.Account < 2800)
                    _poorBots.Add((uint)bot.Index);
            }
        }
        return HookResult.Continue;
    }

    // * Records the time when the freeze period ends
    private HookResult OnFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        _freezeEndTime = Server.CurrentTime;
        BuyRoundGrenades();
        // Strict buy/hold/throw: retry the purchase a few times inside the buy
        // window so late-buying bots still get their grenades, without ever
        // granting free items at throw time.
        int buyRound = _inventoryRound;
        AddTimer(0.5f, () => RetryBuyRoundGrenades(buyRound));
        AddTimer(1.5f, () => RetryBuyRoundGrenades(buyRound));
        AddTimer(2.5f, () => RetryBuyRoundGrenades(buyRound));
        _roundNadeMoneyPerBot.Clear();
        foreach (var bot in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!bot.IsValid || !bot.IsBot) continue;
            var money = bot.InGameMoneyServices;
            if (money == null) continue;
            _roundNadeMoneyPerBot[(uint)bot.Index] = Math.Max(0, money.Account);
        }
        return HookResult.Continue;
    }

    // * Checks whether a bot still has enough freeze-end money for a grenade
    private void RetryBuyRoundGrenades(int round)
    {
        if (round != _inventoryRound || _roundOver) return;
        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if (rules == null || rules.WarmupPeriod || rules.BuyTimeEnded) return;
        BuyRoundGrenades();
    }
    private bool HasLockedNadeMoney(uint botIdx, int cost)
        => _roundNadeMoneyPerBot.TryGetValue(botIdx, out int availableMoney)
            && availableMoney >= cost;

    // * Charges a grenade against the bot's freeze-end money allowance
    private void SpendLockedNadeMoney(uint botIdx, int cost)
    {
        if (!_roundNadeMoneyPerBot.TryGetValue(botIdx, out int availableMoney)) return;
        _roundNadeMoneyPerBot[botIdx] = Math.Max(0, availableMoney - cost);
    }

    // * Stops grenade processing after the round ends
    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        _roundOver = true;
        CompleteNadeLearning(@event.Winner);
        return HookResult.Continue;
    }

    // A dead player makes no more sound, so drop their event state immediately
    // * Removes sound information retained for a dead player
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        RecordLearningDeath(@event);
        if (player != null && player.IsValid)
            _playerSounds.Remove((uint)player.Index);
        return HookResult.Continue;
    }

    // * Determines whether the current round is a pistol round
    private bool IsPistolRound()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;
            if (gameRules == null) return false;

            int played    = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            if (maxRounds   <= 0) maxRounds   = 24;

            int half   = maxRounds / 2;

            return played == 0
                || played == half;
        }
        catch { return false; }
    }

    // * Calculates the grenade spending cap for the current round
    private int GetRoundSpendCap(bool isCT, bool isPoor)
    {
        if (IsPistolRound()) return 800;
        // Poor bots get a lower spend cap
        if (isPoor) return 500;

        var costTable = isCT ? CostCT : CostT;
        int cap = costTable["flash"]
                + costTable["smoke"]
                + costTable["he"]
                + costTable["molotov"];
        return cap;
    }
    // Don't blind ourselves and our teammates
    // * Cancels friendly flashes covered by temporary bot immunity
    private HookResult OnPlayerBlind(EventPlayerBlind @event, GameEventInfo info)
    {
        var victim   = @event.Userid;

        if (victim is null || !victim.IsValid || !victim.IsBot)
            return HookResult.Continue;
        // In case the bot has been taken over
        bool isTakenOver = victim.HasBeenControlledByPlayerThisRound;
        if (isTakenOver)
            return HookResult.Continue;

        var pawn = victim.PlayerPawn?.Value;
        if (_botFlashImmunityUntil.TryGetValue((uint)victim.Index, out float immuneUntil)
            && Server.CurrentTime <= immuneUntil)
        {
            if (pawn != null && pawn.IsValid)
            {
                @event.BlindDuration = 0f;

                ref float blindStartTime = ref pawn.BlindStartTime;
                blindStartTime = 0f;

                ref float blindUntilTime = ref pawn.BlindUntilTime;
                blindUntilTime = 0f;

                ref float flashDuration = ref pawn.FlashDuration;
                flashDuration = 0f;

                ref float flashMaxAlpha = ref pawn.FlashMaxAlpha;
                flashMaxAlpha = 0f;
            }
        }
        return HookResult.Continue;
    }
    // ═══════════════════════════════════════════════════════════
    //  Tick
    // ═══════════════════════════════════════════════════════════

    // * Schedules periodic information, zone, and cooldown maintenance
    private void OnTick()
    {
        _tick++;
        if (_botNadesMode == "off")
        {
            if (_tick % 256 == 0) PruneCooldowns();
            return;
        }
        // Purchase watchdog: every ~0.5 s, independent of FreezeEnd event delivery,
        // retry buying inside the legal buy window. Round changes are detected from
        // rules.TotalRoundsPlayed so this also works on local listen servers.
        if(_tick % 32 == 0)
        {
            var buyRules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
            if(buyRules!=null && !buyRules.WarmupPeriod)
            {
                int round=buyRules.TotalRoundsPlayed;
                if(_lastKnownRound!=round)
                {
                    _lastKnownRound=round;
                    _roundStartTime=Server.CurrentTime;
                    _freezeEndTime=0f;
                    _lastBuyAttempt=0f;
                }
                float nowTick2=Server.CurrentTime;
                // Continue checking the real buy window: a human may request a
                // gun late in freeze time, after the old arbitrary 14s cutoff.
                bool inBuyWindow=nowTick2-_roundStartTime>=UtilityPurchasePolicy.EarliestPurchaseSeconds;
                if(!_roundOver && !buyRules.BuyTimeEnded && inBuyWindow && nowTick2-_lastBuyAttempt>=1.2f)
                {
                    _lastBuyAttempt=nowTick2;
                    BuyRoundGrenades();
                }
            }
        }        if (_tick % 4   == 0) CheckBotZones();
        if (_tick % 256 == 0) PruneCooldowns();
    }
}
