using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using System;

namespace BotState;

public partial class BotState
{

    private readonly Dictionary<int, (float X,float Y)> _travelDirection = new();
    private readonly HashSet<int> _airCrouchOwned = new();
    private readonly Dictionary<int, float> _ladderExitTime = new();
    private readonly Dictionary<int, float> _doorEventCooldown = new();

    private readonly Dictionary<int, float> _stuckStartTime = new();
    private readonly Dictionary<int, (float X, float Y)> _stuckStartPos = new();
    private readonly Dictionary<int, int> _stuckJumpCount = new();
    private readonly Dictionary<int, float> _stuckMaxSpeed = new();
    private readonly Dictionary<int, float> _idleStartTime = new();
    private readonly Dictionary<int, float> _lastRepathTime = new();


    // Temporarily leaves movement to the engine after a Bot opens a door.
    private HookResult OnDoorOpen(EventDoorOpen @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        int idx = (int)player.Index;
        _doorEventCooldown[idx] = Server.CurrentTime + 1.0f;
        return HookResult.Continue;
    }

    // Temporarily leaves movement to the engine after a Bot closes a door.
    private HookResult OnDoorClose(EventDoorClose @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player == null || !player.IsValid || !player.IsBot) return HookResult.Continue;

        int idx = (int)player.Index;
        _doorEventCooldown[idx] = Server.CurrentTime + 1.0f;
        return HookResult.Continue;
    }

    // Updates ladder and air movement; door cooldown skips all later movement work.
    private bool UpdateBotMovement(CCSPlayerPawn pawn, CCSBot bot, int idx, bool curIsAttacking, bool inDoorCooldown)
    {
        // Ladder Stuck Issue Fix
        var moveServices = pawn.MovementServices as CCSPlayer_MovementServices;
        var ladderNormal = moveServices?.LadderNormal;

        bool nearLadder = pawn.MoveType == MoveType_t.MOVETYPE_LADDER
                    || (ladderNormal != null
                        && (ladderNormal.X != 0f || ladderNormal.Y != 0f || ladderNormal.Z != 0f));

        if (nearLadder) _ladderExitTime[idx] = Server.CurrentTime;

        bool inLadderCooldown = nearLadder
            || (_ladderExitTime.TryGetValue(idx, out float exitTime)
                && Server.CurrentTime - exitTime < 5.0f);

        bool inAir = !inLadderCooldown
                && (pawn.GroundEntity == null || !pawn.GroundEntity.IsValid);

        // Restore only the jump crouch we introduced, not native crouch.
        if(!inAir&&_airCrouchOwned.Remove(idx)&&!curIsAttacking)bot.IsCrouching=false;
        // Door Stuck Issue Fix
        if (inDoorCooldown)
        {
            return false;
        }
        // View direction is not travel direction. Native air steering owns XY
        // velocity; the former view-relative boost pushed a turning bot off jumps.
        float speedSquared=pawn.AbsVelocity.X*pawn.AbsVelocity.X+pawn.AbsVelocity.Y*pawn.AbsVelocity.Y;
        if(!inAir&&!nearLadder&&!bot.IsStuck&&speedSquared>40*40)
        {
            float speed=MathF.Sqrt(speedSquared);
            _travelDirection[idx]=(pawn.AbsVelocity.X/speed,pawn.AbsVelocity.Y/speed);
        }
        if(inAir&&!curIsAttacking&&!pawn.IsDefusing&&pawn.AbsVelocity.Z>20&&!bot.IsCrouching)
        {bot.IsCrouching=true;_airCrouchOwned.Add(idx);}
        return true;
    }

    // Recovers stuck bots and periodically repaths idle bots.
    private void RecoverStuckBot(CCSPlayerController player, CCSPlayerPawn pawn, CCSBot bot, int idx, float now, bool curIsAttacking)
    {
        if(curIsAttacking||bot.IsEnemyVisible||pawn.IsDefusing||(pawn.Flags&1)==0
            ||pawn.MoveType==MoveType_t.MOVETYPE_LADDER
            ||(_botController!=null&&BotControllerBridge.IsViewControlled(_botController,player.Slot)))return;
        // Normal Un-Stuck Process
        ref bool isStuck = ref bot.IsStuck;
        if (isStuck)
        {
            ref bool isRunning = ref bot.IsRunning;
            isRunning = true;

            // Preserve the native jump cooldown and approach timing. Rewriting
            // these every tick restarts jump attempts before alignment completes.
            // Manual Stuck State
            float speed2D = MathF.Sqrt(
                pawn.AbsVelocity.X * pawn.AbsVelocity.X +
                pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);

            var curPos = pawn.AbsOrigin!;

            if (!_stuckStartTime.ContainsKey(idx))
            {
                _stuckStartTime[idx] = now;
                _stuckStartPos[idx] = (curPos.X, curPos.Y);
                _stuckMaxSpeed[idx] = 0f;
            }

            if (speed2D > _stuckMaxSpeed.GetValueOrDefault(idx))
                _stuckMaxSpeed[idx] = speed2D;

            float elapsed = now - _stuckStartTime.GetValueOrDefault(idx);
            var sp = _stuckStartPos.GetValueOrDefault(idx, (curPos.X, curPos.Y));
            float dist2D = MathF.Sqrt(
                MathF.Pow(curPos.X - sp.X, 2) +
                MathF.Pow(curPos.Y - sp.Y, 2));
            float maxSpd = _stuckMaxSpeed.GetValueOrDefault(idx);

            bool condA = elapsed >= 1.0f && maxSpd <= 10f;
            bool condB = elapsed >= 3.0f && maxSpd > 10f && dist2D < 75f;

            if ((condA || condB) && (pawn.Flags&1)!=0 && !curIsAttacking && !bot.IsEnemyVisible
                && !pawn.IsDefusing && pawn.MoveType==MoveType_t.MOVETYPE_WALK)
            {
                ref bool isCrouching = ref bot.IsCrouching;
                isCrouching = false;


                int jumpCount = _stuckJumpCount.GetValueOrDefault(idx);
                _stuckJumpCount[idx] = jumpCount + 1;

                float sideSign = (jumpCount % 2 == 0) ? 1f : -1f;
                float offsetRad = 30f * MathF.PI / 180f * sideSign;
                var heading=_travelDirection.GetValueOrDefault(idx);
                // No known approach direction: repath without inventing a
                // backwards impulse from an unrelated sound/combat view angle.
                float baseYaw=MathF.Atan2(heading.Y,heading.X);
                float backYaw = baseYaw + MathF.PI + offsetRad;

                if(heading.X*heading.X+heading.Y*heading.Y>.5f)
                {
                    pawn.AbsVelocity.X = MathF.Cos(backYaw) * 100f;
                    pawn.AbsVelocity.Y = MathF.Sin(backYaw) * 100f;
                }

                CountdownTimer repathTimer = bot.RepathTimer;

                ref float repathduration = ref repathTimer.Duration;
                repathduration = 0.0f;

                ref float repathtimestamp = ref repathTimer.Timestamp;
                repathtimestamp = Server.CurrentTime;

                ref float repathtimescale = ref repathTimer.Timescale;
                repathtimescale = 1.0f;

                // Reset
                _stuckStartTime[idx] = now;
                _stuckStartPos[idx] = (curPos.X, curPos.Y);
                _stuckMaxSpeed[idx] = 0f;
            }
        }
        else
        {
            // Clear
            _stuckStartTime.Remove(idx);
            _stuckStartPos.Remove(idx);
            _stuckMaxSpeed.Remove(idx);
            _stuckJumpCount.Remove(idx);

            // Idle repath: if speed < 5 for 5s, force a repath
            float speed2DIdle = MathF.Sqrt(
                pawn.AbsVelocity.X * pawn.AbsVelocity.X +
                pawn.AbsVelocity.Y * pawn.AbsVelocity.Y);

            if (speed2DIdle < 5f)
            {
                if (!_idleStartTime.ContainsKey(idx))
                    _idleStartTime[idx] = now;

                float idleElapsed = now - _idleStartTime[idx];
                float lastRepath = _lastRepathTime.GetValueOrDefault(idx, -999f);

                if (idleElapsed >= 5f && now - lastRepath >= 5f && !curIsAttacking && !pawn.IsDefusing)
                {
                    ref bool isCrouching = ref bot.IsCrouching;
                    isCrouching = false;

                    _lastRepathTime[idx] = now;

                    CountdownTimer repathTimer = bot.RepathTimer;

                    ref float repathduration = ref repathTimer.Duration;
                    repathduration = 0.0f;

                    ref float repathtimestamp = ref repathTimer.Timestamp;
                    repathtimestamp = Server.CurrentTime;

                    ref float repathtimescale = ref repathTimer.Timescale;
                    repathtimescale = 1.0f;

                    ResetLookAroundForBot(player);
                }
            }
            else
            {
                _idleStartTime.Remove(idx);
            }
        }

    }

    // Retains the Inferno sewer-specific repath adjustment.
    private static void ApplyMapMovementFix(CCSPlayerPawn pawn, CCSBot bot)
    {
        //Inferno Sewer Stuck Fix
        if (pawn.AbsOrigin != null)
        {
            Vector pos = pawn.AbsOrigin;
            bool isInferno = string.Equals(Server.MapName, "de_inferno", StringComparison.OrdinalIgnoreCase);
            float dx = pos.X - 285f;
            float dy = pos.Y - 450f;
            float dist = MathF.Sqrt(dx * dx + dy * dy);

            if (isInferno && dist < 50f)
            {
                CountdownTimer repathTimer = bot.RepathTimer;

                ref float repathduration = ref repathTimer.Duration;
                repathduration = 0.0f;

                ref float repathtimestamp = ref repathTimer.Timestamp;
                repathtimestamp = Server.CurrentTime;

                ref float repathtimescale = ref repathTimer.Timescale;
                repathtimescale = 1.0f;
            }
        }
    }

    // Clears movement state owned by the completed round.
    private void ResetMovementRoundState()
    {
        // Player indices are reused, so do not carry movement state into the next round.
        _travelDirection.Clear();
        _airCrouchOwned.Clear();
        _ladderExitTime.Clear();
        _doorEventCooldown.Clear();
        _stuckStartTime.Clear();
        _stuckStartPos.Clear();
        _stuckJumpCount.Clear();
        _stuckMaxSpeed.Clear();
        _idleStartTime.Clear();
        _lastRepathTime.Clear();
    }
}
