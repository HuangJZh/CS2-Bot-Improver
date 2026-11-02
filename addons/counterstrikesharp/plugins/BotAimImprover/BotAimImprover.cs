using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;


namespace BotAimImprover;

[MinimumApiVersion(305)]
public partial class BotAimImprover : BasePlugin
{
    public override string ModuleName => "BotAimImprover";
    public override string ModuleVersion => "2.1.8-human.8.42-experimental";
    public override string ModuleAuthor => "ed0ard & htfy96 & XBribo & unicbm";
    public override string ModuleDescription => "Restores intelligent aim part selection for CS2 bots.";

    // ============================================================
    // Full-body derived aim points. Each point is defined in the enemy's local frame:
    //   pos.xy = origin.xy + RIGHT * Lateral   (RIGHT = player's right, from yaw)
    //   pos.z  = origin.z  + eyeZ * Frac        (FeetAbsRise>0 means absolute z+rise)
    // Heights (Frac of live eyeZ) come from tm_phoenix/ctm_sas spine bone world heights;
    // lateral offsets from hitbox radii + measured shoulder/elbow widths.
    // Index in this array is the part id used everywhere else.
    // ============================================================
    private readonly struct AimPoint
    {
        public readonly string Name;
        public readonly float Frac;        // height as fraction of live eyeZ (ignored if FeetAbs)
        public readonly float Lateral;     // +right / -left, world units
        public readonly bool FeetAbs;     // true => z = origin.z + Frac (absolute rise), lateral 0
        public AimPoint(string n, float f, float lat, bool feetAbs = false)
        { Name = n; Frac = f; Lateral = lat; FeetAbs = feetAbs; }
    }

    private static readonly AimPoint[] _aimPoints =
    {
        new("HEAD",           1.00f,  0f),   // 0
        new("NECK",           0.97f,  0f),   // 1
        new("JAW",            0.92f,  0f),   // 2
        new("CHEST",          0.82f,  0f),   // 3
        new("GUT",            0.67f,  0f),   // 4
        new("PELVIS",         0.60f,  0f),   // 5
        new("LEFT_CHEST",     0.82f, -8f),   // 6
        new("RIGHT_CHEST",    0.82f,  8f),   // 7
        new("LEFT_SHOULDER",  0.92f, -8f),   // 8
        new("RIGHT_SHOULDER", 0.92f,  8f),   // 9
        new("LEFT_GUT",       0.67f, -7f),   // 10
        new("RIGHT_GUT",      0.67f,  7f),   // 11
        new("LEFT_THIGH",     0.38f, -5f),   // 12
        new("RIGHT_THIGH",    0.38f,  5f),   // 13
        new("LEFT_SHIN",      0.15f, -5f),   // 14
        new("RIGHT_SHIN",     0.15f,  5f),   // 15
        new("FEET",           5.0f,   0f, true), // 16  // absolute z + 5
        new("HEAD_TOP",       1.04f,  0f),   // 17  // top of skull, visible when only the head peeks
        new("HEAD_LEFT",      1.00f, -4f),   // 18
        new("HEAD_RIGHT",     1.00f,  4f),   // 19
    };
    // Priority orders (values are indices into _aimPoints), highest priority first.
    // Tiers: core > centerline > side > shoulder > limb > feet.
    // Within a tier, higher points come first. Left/right of equal height share a tier

    private static readonly int[] _priorityHead =
    {
        0, 17, 18, 19, 1, 2, // HEAD, HEAD_TOP/L/R, NECK, JAW
        3, 4, 5,         // CHEST, GUT, PELVIS
        6, 7, 10, 11,    // L_CHEST, R_CHEST, L_GUT, R_GUT
        8, 9,            // L_SHOULDER, R_SHOULDER
        12, 13, 14, 15,  // L_THIGH, R_THIGH, L_SHIN, R_SHIN
        16               // FEET
    };

    private static readonly int[] _priorityJaw =
    {
        2, 1, 0,         // JAW, NECK, HEAD
        3, 4, 5,         // CHEST, GUT, PELVIS
        6, 7, 10, 11,    // L_CHEST, R_CHEST, L_GUT, R_GUT
        8, 9,            // L_SHOULDER, R_SHOULDER
        12, 13, 14, 15,  // L_THIGH, R_THIGH, L_SHIN, R_SHIN
        16               // FEET
    };

    private static readonly int[] _priorityTrunk =
    {
        4, 5, 3,         // GUT, PELVIS, CHEST,
        10, 11, 6, 7,    // L_GUT, R_GUT, L_CHEST, R_CHEST
        8, 9,            // L_SHOULDER, R_SHOULDER
        2, 1, 0,         // JAW, NECK, HEAD
        12, 13, 14, 15,  // L_THIGH, R_THIGH, L_SHIN, R_SHIN
        16               // FEET
    };

    private static readonly int[] _priorityUpperChest =
    {
        3,6,7,4,10,11,5,8,9, // Upper chest first, retaining exposed-head fallback.
        0,17,18,19,1,2,12,13,14,15,16
    };

    private static readonly int[] _priorityBody =
    {
        4, 5, 10, 11,    // GUT, PELVIS, L_GUT, R_GUT
        3, 6, 7,         // CHEST, L_CHEST, R_CHEST
        8, 9,            // L_SHOULDER, R_SHOULDER
        2, 17, 18, 19, 1, 0, // JAW, HEAD_TOP/L/R, NECK, HEAD        
        12, 13, 14, 15,  // L_THIGH, R_THIGH, L_SHIN, R_SHIN
        16               // FEET
    };
    // ============================================================
    // PickNewAimSpot signatures
    // ============================================================
    private const string LinuxSignature =
        "55 48 89 E5 41 55 41 54 53 48 89 FB 48 83 EC 58 8B 8F ? ? 00 00 83 F9 FF";

    private const string WindowsSignature =
        "48 8B C4 55 57 48 8D 68 ? 48 81 EC ? ? ? ? 48 8B F9 0F 29 70 ? 8B 89 ? ? ? ? 83 F9 FF";

    private MemoryFunctionVoid<IntPtr>? _pickNewAimSpot;
    // Cache: CCSBot* -> controller entity serial + index, independent of client roster/UserId.
    // Cleared on round_start and per-bot on disconnect.
    private readonly ConcurrentDictionary<IntPtr, (uint Controller,int Index)> _botToController = new();

    // Aim mode controlled by the `bot_aim` console command:
    //   Mixed = stable per-engagement head/upper-chest choice; Head = head-first
    //   Body  = always body-first
    private enum AimMode { MIXED, HEAD, BODY }
    private AimMode _aimMode = AimMode.MIXED;


    // One-shot flag so we log a single confirmation that overrides are actually firing.
    private bool _firstOverrideLogged = false;

    // ============================================================
    // Lifecycle
    // ============================================================

    public override void Load(bool hotReload)
    {
        bool win = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        try
        {
            // Reject missing fields before installing a hook that can write native memory.
            if (Schema.GetSchemaOffset("CCSPlayerPawn", "m_pBot") <= 0
                || Schema.GetSchemaOffset("CCSBot", "m_targetSpot") <= 0
                || Schema.GetSchemaOffset("CCSBot", "m_enemy") <= 0
                || Schema.GetSchemaOffset("CCSBot", "m_isEnemyVisible") <= 0)
                throw new InvalidOperationException("Required bot Schema fields are missing.");

            _pickNewAimSpot = new MemoryFunctionVoid<IntPtr>(win ? WindowsSignature : LinuxSignature);

            long pnaRuntime = _pickNewAimSpot.Handle.ToInt64();
            if (pnaRuntime == 0)
                throw new InvalidOperationException("PickNewAimSpot signature resolved to zero address.");

            _pickNewAimSpot.Hook(OnPickNewAimSpotPost, HookMode.Post);

            Logger.LogInformation("[BotAimImprover] Loaded ({Plat}). PickNewAimSpot=0x{Pna:X16}",
                win ? "Windows" : "Linux", pnaRuntime);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BotAimImprover] Fatal error during Load() (signature or Schema unavailable). Plugin inactive.");
            return;
        }

        RegisterEventHandler<EventRoundStart>((_, _) =>
        {
            _botToController.Clear();
            ClearSmokeError();
            ResetHuman();
            _enemyElites.Reset();
            var anchor=EntityPlayers().Where(p=>p.IsValid&&p.TeamNum is 2 or 3).OrderBy(p=>p.Index).FirstOrDefault();
            _routeLearning.BeginRound(anchor?.EntityHandle.Raw??0,anchor?.TeamNum??0);
            return HookResult.Continue;
        });

        RegisterEventHandler<EventPlayerDisconnect>((ev, _) =>
        {
            try
            {
                var disconnecter = ev.Userid;
                if (disconnecter is {IsValid:true})
                {
                    uint leavingController=disconnecter.EntityHandle.Raw;
                    foreach (var kv in _botToController)
                        if (kv.Value.Controller == leavingController)
                            _botToController.TryRemove(kv.Key,out var removed);
                }
            }
            catch { /* non-fatal */ }
            return HookResult.Continue;
        });

        RegisterListener<Listeners.OnMapStart>(_ => { _botToController.Clear();ClearSmokeError();ResetHuman();_enemyElites.Reset();_routeLearning.Clear(); });
        InitializeHumanAim();
        AddCommand("bot_aim", "Set bot aim mode: head, body, mixed", (caller, info) =>
        {
            string arg = info.ArgCount > 1 ? info.GetArg(1).Trim().ToLowerInvariant() : "";
            string reply;
            switch (arg)
            {
                case "head":
                    _aimMode = AimMode.HEAD;
                    reply = "[BotAimImprover] aim mode -> HEAD (always head-first)";
                    break;
                case "body":
                    _aimMode = AimMode.BODY;
                    reply = "[BotAimImprover] aim mode -> BODY (always body-first)";
                    break;
                case "mixed":
                    _aimMode = AimMode.MIXED;
                    reply = "[BotAimImprover] aim mode -> MIXED (default)";
                    break;
                default:
                    reply = $"[BotAimImprover] Current aim mode: {_aimMode}. Valid values: head, body, mixed";
                    break;
            }
            Server.PrintToConsole(reply);
        });
    }

    public override void Unload(bool hotReload)
    {
        StopAimTelemetry();
        UninstallNativePhases();
        ResetHuman();
        _pendingGuardReleases.Clear();
        try { _updateLook?.Unhook(OnHumanLook,HookMode.Pre); } catch { }
        try { _updateLook?.Unhook(OnHumanLookPost,HookMode.Post); } catch { }
        try { _pickNewAimSpot?.Unhook(OnPickNewAimSpotPost, HookMode.Post); }
        catch { /* ignore */ }
    }

    // ============================================================
    // Core override logic (Post-hook on PickNewAimSpot)
    //
    // Native function already set m_targetSpot to GUT or HEAD based on
    // mp_damage_headshot_only. We re-pick based on visible enemy parts and the
    // bot's weapon, then overwrite only the 12 bytes of m_targetSpot.
    // ============================================================
    private HookResult OnPickNewAimSpotPost(DynamicHook hook)
    {
        try
        {
            IntPtr pCCSBot = hook.GetParam<IntPtr>(0);
            if (pCCSBot == IntPtr.Zero)
                return HookResult.Continue;

            var bot = new CCSBot(pCCSBot);

            // 1) Gate: enemy must be generally visible before we
            //    spend any raytraces. Otherwise the native used last-known position.


            // 2) Resolve enemy pawn from m_enemy CHandle.
            var enemy = bot.Enemy;
            if (!enemy.IsValid)
                return HookResult.Continue;

            uint enemyIdx = enemy.Index;
            if (enemyIdx <= 0 || enemyIdx >= 4096)
                return HookResult.Continue;

            CCSPlayerPawn? enemyPawn = enemy.Value;
            if (enemyPawn == null || !enemyPawn.IsValid || enemyPawn.Handle == IntPtr.Zero)
                return HookResult.Continue;

            // 3) Resolve the bot's controller (for weapon + eye position).
            var botController = ResolveBotController(pCCSBot);
            if (botController == null || !TryGetBotEyePosition(botController, out var botEye))
                return HookResult.Continue;

            if (WriteCachedHumanSpot(bot,botController,botEye)) return HookResult.Continue;
            if (!botController.HasBeenControlledByPlayerThisRound && ApplySmokeError(bot, enemyPawn, botEye))
                return HookResult.Continue;
            if (!bot.IsEnemyVisible) return HookResult.Continue;

            if(botController.PlayerPawn.Value is not {IsValid:true} ownPawn)return HookResult.Continue;
            string? wpn=ownPawn.WeaponServices?.ActiveWeapon.Value?.DesignerName;
            int[] order = AimOrder(botController,ownPawn,enemyPawn);

            // 5) Walk the priority order and raytrace each point from the bot's
            // eye; the first visible point wins.
            int chosenIdx = -1;
            float rx = 0f, ry = 0f, rz = 0f;
            foreach (int idx in order)
            {
                if (!TryComputePartPos(enemyPawn, idx, out float x, out float y, out float z))
                    continue;
                if (!PointVisibleFromEye(botEye, x, y, z))
                    continue;
                chosenIdx = idx;
                rx = x; ry = y; rz = z;
                break;
            }
            if (chosenIdx < 0)
                return HookResult.Continue;

            // 6) Overwrite only m_targetSpot.xyz.
            var targetSpot = bot.TargetSpot;
            targetSpot.X = rx; targetSpot.Y = ry; targetSpot.Z = rz;

            // One-time confirmation that the override path actually runs end-to-end.
            if (!_firstOverrideLogged)
            {
                _firstOverrideLogged = true;
                Logger.LogInformation(
                    "[BotAimImprover] Active: first override (weapon={W} point={P}).",
                    wpn ?? "(null)", _aimPoints[chosenIdx].Name);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[BotAimImprover] Exception in PostHook");
        }

        return HookResult.Continue;
    }

    /// Find the CCSPlayerController whose pawn's m_pBot field equals pCCSBot.
    /// Cache identity follows native controller handles, not the connection roster.
    private static CCSPlayerController[] EntityPlayers() => Utilities
        .FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
        .Where(p=>p.IsValid).ToArray();
    private static CCSPlayerController? EntityPlayerAtIndex(int index)
    {
        var p=Utilities.GetEntityFromIndex<CCSPlayerController>(index);
        return p is {IsValid:true} && p.DesignerName=="cs_player_controller" ? p : null;
    }
    private static CCSPlayerController? EntityPlayerAtSlot(int slot)
        => slot<0 ? null : EntityPlayerAtIndex(slot+1);
    private CCSPlayerController? ResolveBotController(IntPtr pointer)
    {
        if(_botToController.TryGetValue(pointer,out var cached))
        {
            var player=EntityPlayerAtIndex(cached.Index);
            if(player is {IsBot:true} && player.EntityHandle.Raw==cached.Controller
                && player.PlayerPawn.Value is {IsValid:true} pawn && pawn.Bot?.Handle==pointer)return player;
            _botToController.TryRemove(pointer,out _);
        }
        foreach(var player in EntityPlayers())
        {
            if(!player.IsBot || player.PlayerPawn.Value is not {IsValid:true} pawn
                || pawn.Bot?.Handle!=pointer)continue;
            _botToController[pointer]=(player.EntityHandle.Raw,(int)player.Index);
            return player;
        }
        return null;
    }

    // Bot eye position = bot pawn origin + view offset Z.
    private static bool TryGetBotEyePosition(CCSPlayerController bot, out Vector eye)
    {
        eye = new Vector(0, 0, 0);
        var pawn = bot.PlayerPawn?.Value;
        var origin = pawn?.AbsOrigin;
        if (origin == null) return false;
        float ez = pawn!.ViewOffset?.Z ?? 64.0f;
        eye = new Vector(origin.X, origin.Y, origin.Z + ez);
        return true;
    }

    // Compute world position of derived point `idx` from the enemy pawn's schema fields.
    private static bool TryComputePartPos(CCSPlayerPawn enemyPawn, int idx,
                                          out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (idx < 0 || idx >= _aimPoints.Length) return false;
        var origin = enemyPawn.AbsOrigin;
        if (origin == null) return false;

        ref readonly AimPoint p = ref _aimPoints[idx];
        float ox = origin.X, oy = origin.Y, oz = origin.Z;
        float eyeZ = enemyPawn.ViewOffset?.Z ?? 64.0f;

        float yawDeg = enemyPawn.EyeAngles?.Y ?? 0.0f;
        double yawRad = yawDeg * Math.PI / 180.0;
        float rX = (float)Math.Sin(yawRad);   // RIGHT vector x
        float rY = (float)-Math.Cos(yawRad);  // RIGHT vector y

        if (p.FeetAbs)
        {
            x = ox; y = oy; z = oz + p.Frac;   // absolute rise (FEET)
        }
        else
        {
            x = ox + rX * p.Lateral;
            y = oy + rY * p.Lateral;
            z = oz + eyeZ * p.Frac;
        }
        return !(float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)
                 || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z));
    }

    // Fraction/lateral variant used by the fallback sampler for body/arm points
    // that are not part of the fixed aim-point table.
    private static bool TryComputeOffsetPos(CCSPlayerPawn enemyPawn, float frac, float lateral,
                                            out float x, out float y, out float z)
    {
        x = y = z = 0;
        var origin = enemyPawn.AbsOrigin;
        if (origin == null) return false;
        float eyeZ = enemyPawn.ViewOffset?.Z ?? 64.0f;
        float yawDeg = enemyPawn.EyeAngles?.Y ?? 0.0f;
        double yawRad = yawDeg * Math.PI / 180.0;
        float rX = (float)Math.Sin(yawRad);
        float rY = (float)-Math.Cos(yawRad);
        x = origin.X + rX * lateral;
        y = origin.Y + rY * lateral;
        z = origin.Z + eyeZ * frac;
        return !(float.IsNaN(x) || float.IsNaN(y) || float.IsNaN(z)
                 || float.IsInfinity(x) || float.IsInfinity(y) || float.IsInfinity(z));
    }

    // World-only LoS test from eye to target point, true if unobstructed (>= 0.999)
    private bool PointVisibleFromEye(Vector eye, float tx, float ty, float tz)
        => PointVisibleFromEye(eye, tx, ty, tz, 0.999f, out _);

    // Relaxed variant used only by the partial-cover fallback: a fraction a few
    // percent short of the full eye-to-point distance still means the candidate is
    // effectively in line of sight, not that the target is behind a wall.
    private bool PointVisibleFromEye(Vector eye, float tx, float ty, float tz, float minFraction, out float fraction)
    {
        fraction = 0f;
        try
        {
            var end = new Vector(tx, ty, tz);
            var opts = new TraceOptions { InteractsWith = Masks.SolidBrushOnly };
            var res = Trace.TraceEndShape(eye, end, options: opts);
            fraction = res.Fraction;
            return fraction >= minFraction;
        }
        catch { return false; }
    }

}
