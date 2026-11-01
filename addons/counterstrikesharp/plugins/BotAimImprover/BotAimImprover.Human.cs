using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text.Json;
using Vec=System.Numerics.Vector3;
using Angles=System.Numerics.Vector2;

namespace BotAimImprover;
public partial class BotAimImprover
{
    private sealed class HumanSettings
    {
        public bool Enabled {get;set;}=true;
        public bool Diagnostics {get;set;}=false;
        public bool DirectViewControl {get;set;}=false; // Experimental until recoil/weapon checks are validated.
        public float HorizontalFieldOfView {get;set;}=110;
    }
    private sealed class HumanState
    {
        public required uint Pawn,Enemy;
        public uint PlannedWeapon;
        public required nint Pointer;
        public required HumanAimModel Model;
        public float LastUsed;
        public float NextSensorCheck;
        public bool LastSight;
        public AimOutput Output;
        public VisualEstimate? LastVisualEstimate;
        public float UnderFireUntil;
        public float? FirstVisualAt;
        public float? PlanHoldSince;
        public bool PlanHoldTimedOut;
        public string LastSampleResult="none";
        public int LastSampleAttempts;
        public string LastSamplePoint="none";
        public float? LastSampleRayFraction;
        public float AllowLatchUntil;
        public float LastAimFeedbackCorrection;
        public float LastShotAt=-999f;
        public int SprayShots;
        public float SprayBasePitchError;
        public float RecoilCompensation;
        public float BurstCooldownUntil;
        public BurstCadence Cadence=new();
    }
    private HumanSettings _humanSettings=new();
    private readonly Dictionary<int,HumanState> _humanStates=new();
    private readonly Dictionary<string,long> _humanPhases=new();
    private readonly Dictionary<nint,Vec> _modelSpots=new();
    private MemoryFunctionVoid<nint>? _updateLook;
    private IBotControllerApi? _humanController;
    private string _humanDifficulty="Medium";
    private long _humanLookCalls;
    private float _testGateUntil=float.NegativeInfinity;
    private bool TestGateActive=>Server.CurrentTime<_testGateUntil;
    private bool HumanActive => _humanSettings.Enabled && _humanController != null && _updateLook != null && _nativePhasesReady;

    private void InitializeHumanAim()
    {
        ReadHumanSettings();
        InitializeAimTelemetry();
        var processArgs=Environment.GetCommandLineArgs();
        int testPort=Array.IndexOf(processArgs,"-port"),testIp=Array.IndexOf(processArgs,"-ip");
        if(processArgs.Contains("-dedicated") && testPort>=0 && testPort+1<processArgs.Length && processArgs[testPort+1]=="27025"
            && testIp>=0 && testIp+1<processArgs.Length && processArgs[testIp+1]=="127.0.0.1")
            AddCommand("bot_aim_test_gate","Loopback fixture only: block [seconds<=5], release",(caller,c)=>{
                if(caller!=null)return;
                string action=c.ArgCount>1 ? c.GetArg(1) : "status";
                if(action=="block")
                {
                    float seconds=c.ArgCount>2 && float.TryParse(c.GetArg(2),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var value) && float.IsFinite(value) ? Math.Clamp(value,.5f,5) : 3;
                    _testGateUntil=Server.CurrentTime+seconds;
                }
                if(action=="release")_testGateUntil=float.NegativeInfinity;
                WriteTelemetry(new {kind="test_gate_control",tick=Server.TickCount,time=Server.CurrentTime,action,active=TestGateActive,
                    until=TestGateActive ? (float?)_testGateUntil : null});
                Server.PrintToConsole($"[HumanAimTestGate] active={TestGateActive}; expires automatically, local dedicated fixture only");
            });
        // Use the installed BotController gamedata, not an invented/current-upstream signature.
        var path=Path.GetFullPath(Path.Combine(ModuleDirectory,"../../../../addons/BotController/gamedata.json"));
        try
        {
            using var document=JsonDocument.Parse(File.ReadAllText(path));
            var platform=OperatingSystem.IsWindows() ? "windows" : "linux";
            var signature=document.RootElement.GetProperty("CCSBot::UpdateLookAngles").GetProperty("signatures").GetProperty(platform).GetString()!;
            _updateLook=new MemoryFunctionVoid<nint>(signature);
            if(_updateLook.Handle==nint.Zero)throw new InvalidOperationException("UpdateLookAngles unavailable");
            _updateLook.Hook(OnHumanLook,HookMode.Pre);
            InitializeDirectView(document.RootElement,platform);
            _updateLook.Hook(OnHumanLookPost,HookMode.Post);
            try{InitializeNativePhases(document.RootElement,platform);}
            catch(Exception error){UninstallNativePhases();Logger.LogWarning(error,"Native decision hooks unavailable; human aim inactive");}
        }
        catch(Exception error)
        {
            _updateLook=null;
            Logger.LogError(error,"Human aim unavailable; keeping previous aiming behavior");
        }
        RegisterListener<Listeners.OnTick>(() =>
        {
            foreach(var (slot,state) in _humanStates.ToArray())
            {
                var p=Utilities.GetPlayerFromSlot(slot);
                if(p is not {IsValid:true,IsBot:true,PawnIsAlive:true} || p.HasBeenControlledByPlayerThisRound
                    || p.PlayerPawn.Raw!=state.Pawn || Server.CurrentTime-state.LastUsed>.5f)ReleaseHuman(slot);
            }
            MaintainFireGuards();
        });
        RegisterEventHandler<EventPlayerDeath>((e,i) => { if(e.Userid is {} p){ReleaseHuman(p.Slot);ReleaseFireGuard(p.Slot);_soundPreaim.Remove(p.Slot);_attentionTurnAt.Remove(p.Slot);ResetSilentMovement(p.Slot);}return HookResult.Continue; });
        // Track bursts so the aim output can model human recoil control after the
        // first few rounds instead of holding a perfectly steady target point.
        RegisterEventHandler<EventWeaponFire>((e,i) => {
            string eventItem=e.Weapon ?? "";
            if(!eventItem.StartsWith("weapon_"))eventItem="weapon_"+eventItem;
            if(HumanFireContext.Classify(eventItem)==AimWeaponClass.Other)return HookResult.Continue;
            if(e.Userid is {IsValid:true} shooter && _humanStates.TryGetValue(shooter.Slot,out var fireState)
                && fireState.Pawn==shooter.PlayerPawn.Raw)
            {
                float fireNow=Server.CurrentTime;
                float pause=fireState.Model.Profile.BurstPause*(.8f+Random.Shared.NextSingle()*.4f);
                fireState.Cadence.Shot(fireNow,fireState.Model.Profile.BurstLength,pause);
                fireState.SprayShots=fireState.Cadence.Shots;
                fireState.BurstCooldownUntil=fireState.Cadence.PauseUntil;
                fireState.LastShotAt=fireNow;
                if(fireState.SprayShots==1 && shooter.PlayerPawn.Value is {} firePawn)
                    fireState.SprayBasePitchError=firePawn.EyeAngles.X-fireState.Model.LastDesiredAngles.X;
            }
            if(HumanActive && e.Userid is {IsValid:true} soundShooter)
            {
                string weapon=eventItem;
                float range=weapon.Contains("silencer") ? 1400f : weapon.Contains("awp") ? 2800f : 2500f;
                HearSound(soundShooter,range,.95f,"gunfire",Random.Shared.NextSingle()*.12f,1f);
            }
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerSound>((e,i) => {
            if(e.Step && e.Userid is {IsValid:true} walker && e.Radius>0 && e.Duration>0)
                HearFootstep(walker,e.Radius,e.Duration,"player_sound");
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerFootstep>((e,i) => {
            // Defer the compatibility event so a same-frame modern event supplies
            // the authoritative radius, and deduplicate both event paths.
            if(e.Userid is {IsValid:true} walker)
            {
                uint pawn=walker.PlayerPawn.Raw;
                Server.NextFrame(() => {
                    if(walker.IsValid && walker.PlayerPawn.Raw==pawn)HearFootstep(walker,1100f,.5f,"legacy-footstep");
                });
            }
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerHurt>((e,i) => {
            if(e.Userid is {IsValid:true,IsBot:true} victim && e.Attacker is {IsValid:true} attacker && attacker.TeamNum!=victim.TeamNum
                && _humanStates.TryGetValue(victim.Slot,out var state) && state.Pawn==victim.PlayerPawn.Raw)
                state.UnderFireUntil=Server.CurrentTime+.6f;
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerDisconnect>((e,i) => { if(e.Userid is {} p){ReleaseHuman(p.Slot);ReleaseFireGuard(p.Slot);_soundPreaim.Remove(p.Slot);_attentionTurnAt.Remove(p.Slot);ResetSilentMovement(p.Slot);}return HookResult.Continue; });
        RegisterEventHandler<EventItemEquip>((e,i) => {
            if(e.Userid is {IsValid:true} player)
            {
                try{RefreshFireGuard(player);}
                catch(Exception error){_humanSettings.Enabled=false;ResetHuman();Logger.LogError(error,"Human aim disabled after equip guard failure");}
            }
            return HookResult.Continue;
        });
        AddCommand("bot_human_aim","Human aim: status/on/off/reload",(caller,c) =>
        {
            if(!IsLocalAimConsole(caller))return;
            var action=c.ArgCount>1 ? c.GetArg(1).ToLowerInvariant() : "status";
            if(action is "on" or "off") { _humanSettings.Enabled=action=="on";ResetHuman(); }
            if(action=="reload") { ResetHuman();ReadHumanSettings(); }
            if(action is "on" or "off" or "reload")WriteTelemetry(new {kind="model_control",tick=Server.TickCount,time=Server.CurrentTime,action,enabled=_humanSettings.Enabled,active=HumanActive});
            Server.PrintToConsole($"[HumanAim] active={HumanActive} difficulty={_humanDifficulty} direct_requested={_humanSettings.DirectViewControl} direct_available={_setEyeAngles!=null} native_ready={_nativePhasesReady} guards={_fireGuards.Count} pending={_fireGuards.Values.Count(g=>g.Token<=0)} calls={_humanLookCalls} bots={Utilities.GetPlayers().Count(p=>p.IsBot)} alive={Utilities.GetPlayers().Count(p=>p.PawnIsAlive)} states={_humanStates.Count} phases={JsonSerializer.Serialize(_humanPhases)}");
        });
        AddTimer(10f,() => {
            if(_humanSettings.Diagnostics && HumanActive)
                Logger.LogInformation("Human aim diagnostics difficulty={Difficulty} states={States} phases={Phases}",_humanDifficulty,_humanStates.Count,JsonSerializer.Serialize(_humanPhases));
        },CounterStrikeSharp.API.Modules.Timers.TimerFlags.REPEAT);
        ReadHumanDifficulty();
    }
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try { _humanController=new PluginCapability<IBotControllerApi>("botcontroller:api").Get(); }
        catch(Exception error) { Logger.LogWarning(error,"Human aim requires installed BotController API; previous aim retained"); }
        Logger.LogInformation("Human aim enabled={Active} difficulty={Difficulty}",HumanActive,_humanDifficulty);
    }
    private void ReadHumanSettings()
    {
        var path=Path.Combine(ModuleDirectory,"human-aim.json");
        try
        {
            if(File.Exists(path))_humanSettings=JsonSerializer.Deserialize<HumanSettings>(File.ReadAllText(path)) ?? new();
            else File.WriteAllText(path,JsonSerializer.Serialize(_humanSettings,new JsonSerializerOptions{WriteIndented=true}));
            _humanSettings.HorizontalFieldOfView=float.IsFinite(_humanSettings.HorizontalFieldOfView) ? Math.Clamp(_humanSettings.HorizontalFieldOfView,70,140) : 110;
        }
        catch(Exception error) { Logger.LogWarning(error,"Cannot read human-aim settings; keeping defaults"); }
    }
    private void ReadHumanDifficulty()
    {
        var path=Path.GetFullPath(Path.Combine(ModuleDirectory,"../../../../overrides"));
        try
        {
            var active=SHA256.HashData(File.ReadAllBytes(Path.Combine(path,"botprofile.vpk")));
            foreach(var level in new[]{"Low","Medium","High"})
                if(active.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(path,level,"botprofile.vpk")))))
                { _humanDifficulty=level;return; }
            Logger.LogWarning("Human aim did not recognize active pack; keeping {Difficulty}",_humanDifficulty);
        }
        catch(IOException error) { Logger.LogWarning(error,"Human aim difficulty unavailable; keeping {Difficulty}",_humanDifficulty); }
    }
    private void ReleaseHuman(int slot)
    {
        // Any movement override owned by this slot must die with the human-aim
        // state; otherwise a released bot can stay movement-locked (standstill /
        // no reaction) until the round ends.
        if(!_humanStates.Remove(slot,out var state))return;
        _modelSpots.Remove(state.Pointer);
    }
    private void ResetHuman()
    {
        _testGateUntil=float.NegativeInfinity;
        foreach(var slot in _humanStates.Keys.ToArray())ReleaseHuman(slot);
        foreach(var slot in _fireGuards.Keys.ToArray())ReleaseFireGuard(slot);
        _modelSpots.Clear();_humanPhases.Clear();_soundPreaim.Clear();_lastFootstep.Clear();_attentionTurnAt.Clear();
        _preaimTargets.Clear();_preaimVisited.Clear();ResetAllSilentMovement();ReadHumanDifficulty();
    }
    private static bool IsGun(CCSPlayerPawn pawn)
        => pawn.WeaponServices?.ActiveWeapon.Value?.As<CCSWeaponBase>().VData?.GearSlot is gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL;
    private bool OtherViewOwner(int slot) => _humanController!.IsLocked(slot,LockKind.Aim)
        || _humanController.IsLocked(slot,LockKind.All) || _humanController.IsReplaying(slot);

    // Simple sound preaim: an enemy gunshot within earshot sets a short-lived
    // direction estimate. Bots without a live visual target turn toward it with a
    // bounded turn rate and a tier-scaled angular error, instead of ignoring sound.
    private bool TryApplySoundPreaim(CCSPlayerController controller,CCSBot bot,nint pointer,CCSPlayerPawn pawn,Vec eyeVec,float now)
    {
        if(!_soundPreaim.TryGetValue(controller.Slot,out var aim))return false;
        if(now<aim.Start)return false;
        if(now>aim.Until){_soundPreaim.Remove(controller.Slot);return false;}
        var current=new Angles(pawn.EyeAngles.X,pawn.EyeAngles.Y);
        var desired=AimMath.Angles(eyeVec,aim.Position);
        var profile=HumanAimProfile.For(_humanDifficulty);
        var next=AimMath.Advance(current,desired,AttentionDelta(controller.Slot,now),profile.TrackingTime,profile.MaxSpeed);
        var spot=AimMath.Ray(eyeVec,next);
        _modelSpots[pointer]=spot;
        WriteModelSpot(bot,spot);
        RefreshFireGuard(controller);
        return true;
    }
    private HookResult OnHumanLook(DynamicHook hook)
    {
        _humanLookCalls++;
        if(!HumanActive)return HookResult.Continue;
        CCSPlayerController? controller=null;
        try
        {
            var pointer=hook.GetParam<nint>(0);
            controller=ResolveBotController(pointer);
            if(controller is not {IsValid:true,IsBot:true,PawnIsAlive:true} || controller.HasBeenControlledByPlayerThisRound)return HookResult.Continue;
            var pawn=controller.PlayerPawn.Value;
            if(pawn is not {IsValid:true} || !IsGun(pawn) || OtherViewOwner(controller.Slot))
            { ReleaseHuman(controller.Slot);ReleaseFireGuard(controller.Slot);_modelSpots.Remove(pointer);return HookResult.Continue; }
            if(!TryGetBotEyePosition(controller,out var eye))return HookResult.Continue;
            var eyeVec=new Vec(eye.X,eye.Y,eye.Z);
            float now=Server.CurrentTime;
            var bot=new CCSBot(pointer);
            TickSilentMovement(controller,pawn,bot,now);
            if(bot.Enemy.Value is not {IsValid:true} enemy || enemy.LifeState!=0 || enemy.TeamNum==controller.TeamNum)
            {
                if(TryApplySoundPreaim(controller,bot,pointer,pawn,eyeVec,now))return HookResult.Continue;
                if(TryApplyMapPreaim(controller,bot,pointer,pawn,eyeVec,now))return HookResult.Continue;
                // The native enemy handle can flicker between peeks. Keep the memory
                // based plan for a short grace period instead of resetting every
                // model (reset means a fresh sensor delay and reaction before the
                // next shot). No target spot is written while the target is gone.
                if(_humanStates.TryGetValue(controller.Slot,out var held) && held.Pawn==controller.PlayerPawn.Raw
                    && now-held.LastUsed<.35f && held.Model.HasObservation && now-held.Model.LastVisible<.50f)
                {
                    held.LastUsed=now;
                    held.Output=held.Model.Step(now,eyeVec,false,default,new(pawn.EyeAngles.X,pawn.EyeAngles.Y));
                    _modelSpots.Remove(pointer);
                    RefreshFireGuard(controller);
                    return HookResult.Continue;
                }
                ReleaseHuman(controller.Slot);_modelSpots.Remove(pointer);RefreshFireGuard(controller);return HookResult.Continue;
            }
            _preaimTargets.Remove(controller.Slot);
            if(!_humanStates.TryGetValue(controller.Slot,out var state) || state.Pawn!=controller.PlayerPawn.Raw || state.Enemy!=enemy.EntityHandle.Raw)
            {
                ReleaseHuman(controller.Slot);
                state=new HumanState{Pointer=pointer,Pawn=controller.PlayerPawn.Raw,Enemy=enemy.EntityHandle.Raw,
                    PlannedWeapon=pawn.WeaponServices?.ActiveWeapon.Raw ?? uint.MaxValue,
                    Model=new(HumanAimProfile.For(_humanDifficulty),new(pawn.EyeAngles.X,pawn.EyeAngles.Y),now,Random.Shared.Next())};
                _humanStates[controller.Slot]=state;
            }
            state.LastUsed=now;
            bool visible=state.LastSight && bot.IsEnemyVisible && pawn.BlindUntilTime<=now;
            if(now>=state.NextSensorCheck)
            {
                state.NextSensorCheck=now+state.Model.Profile.SampleInterval;
                Vec sample=default;
                float sampleRadius=4;
                bool sensorGate=bot.IsEnemyVisible && pawn.BlindUntilTime<=now;
                string sampleResult=sensorGate ? "not-sampled" : pawn.BlindUntilTime>now ? "blind" : "native-invisible";
                int sampleAttempts=0;
                string samplePoint="none";
                float sampleRayFraction=0f;
                bool smokeBlocked=false;
                bool degradedSmoke=false;
                visible=sensorGate && TrySelectHumanPoint(bot,pawn,enemy,eye,out sample,out sampleRadius,out sampleResult,out sampleAttempts,out samplePoint,out sampleRayFraction,out smokeBlocked);
                if(!visible && sensorGate && smokeBlocked && TryBuildSmokeSample(eyeVec,enemy,out sample,out sampleRadius))
                {
                    // The target is natively visible but smoke blocks every precise
                    // point. Do not fall back to perfect native aim: take a coarse,
                    // deliberately imprecise body-level sample whose large uncertainty
                    // flows through the model's perception/motor pipeline.
                    visible=true;
                    degradedSmoke=true;
                    sampleResult="smoke-degraded";
                    samplePoint="smoke:body";
                    sampleRayFraction=0f;
                }
                state.LastSampleResult=sampleResult;
                state.LastSampleAttempts=sampleAttempts;
                state.LastSamplePoint=samplePoint;
                state.LastSampleRayFraction=sensorGate && sampleAttempts>0 && !degradedSmoke ? sampleRayFraction : null;
                state.LastSight=visible;
                if(visible)
                {
                    // Reacquisition after lost memory is a fresh movement only when the
                    // target was gone long enough that the old plan is no longer usable.
                    // Short peeks keep the plan so reacquiring does not pay a fresh
                    // sensor delay + reaction + flick every time.
                    float visibleGap=state.Model.HasObservation ? now-state.Model.LastVisible : float.PositiveInfinity;
                    if(state.Model.HasObservation && visibleGap>ReacquireResetSeconds)
                    {
                        WriteTelemetry(new {kind="model_lifecycle",tick=Server.TickCount,time=now,slot=controller.Slot,action="reacquire-reset",gap=visibleGap});
                        ReleaseHuman(controller.Slot);
                        state=new HumanState{Pointer=pointer,Pawn=controller.PlayerPawn.Raw,Enemy=enemy.EntityHandle.Raw,
                            PlannedWeapon=pawn.WeaponServices?.ActiveWeapon.Raw ?? uint.MaxValue,
                            Model=new(HumanAimProfile.For(_humanDifficulty),new(pawn.EyeAngles.X,pawn.EyeAngles.Y),now,Random.Shared.Next()),
                            LastUsed=now,LastSight=true,NextSensorCheck=now+HumanAimProfile.For(_humanDifficulty).SampleInterval,
                            UnderFireUntil=state.UnderFireUntil};
                        _humanStates[controller.Slot]=state;
                    }
                    else if(state.Model.HasObservation && visibleGap>.35f)
                        WriteTelemetry(new {kind="model_lifecycle",tick=Server.TickCount,time=now,slot=controller.Slot,action="reacquire-keep",gap=visibleGap});
                    // The sample, not the live entity, enters the model's delay queue.
                    state.LastVisualEstimate=state.Model.ObserveVisual(now,eyeVec,sample,new(pawn.EyeAngles.X,pawn.EyeAngles.Y),sampleRadius,
                        degradedSmoke ? .60f : 0f,samplePoint);
                    if(state.LastVisualEstimate.HasValue)state.FirstVisualAt??=now;
                }
            }
            // A retained native enemy is not a live visual threat. Let a ready
            // sound cue interrupt old visual memory before any early return.
            if(visible)_soundPreaim.Remove(controller.Slot);
            else if(TryApplySoundPreaim(controller,bot,pointer,pawn,eyeVec,now))return HookResult.Continue;
            // Preserve native search/navigation once visual memory is gone. While a
            // recent observation is still usable, keep the memory-based plan alive so
            // the model holds/predicts the last seen angle instead of snapping back to
            // perfect native search aim during short peeks and cover flickers.
            if((!visible && !state.Model.HasObservation) || (!visible && now-state.Model.LastVisible>ReacquireResetSeconds))
            {
                _modelSpots.Remove(pointer);
                state.Output=state.Model.Step(now,eyeVec,false,default,new(pawn.EyeAngles.X,pawn.EyeAngles.Y));
                RefreshFireGuard(controller);
                return HookResult.Continue;
            }
            var fireContext=new HumanFireContext(HumanFireContext.Classify(pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName),now<state.UnderFireUntil);
            state.PlannedWeapon=pawn.WeaponServices?.ActiveWeapon.Raw ?? uint.MaxValue;
            state.Output=state.Model.Step(now,eyeVec,visible,fireContext,new(pawn.EyeAngles.X,pawn.EyeAngles.Y));
            _humanPhases[state.Output.Phase]=_humanPhases.GetValueOrDefault(state.Output.Phase)+1;
            // Execution feedback is measured for calibration but not injected: natural
            // captures show the engine view lags the plan by several degrees and a
            // naive correction of the target spot worsened planned-vs-actual error.
            var actualView=new Angles(pawn.EyeAngles.X,pawn.EyeAngles.Y);
            var viewDelta=AimMath.Delta(state.Model.LastDesiredAngles,actualView);
            state.LastAimFeedbackCorrection=viewDelta.Length();
            // Recoil-control measurement kept for calibration, but the first
            // compensation attempt (8.13) reduced hits from .48 to .21 at 600 u:
            // the engine view already reflects native downward compensation, so
            // correcting the observed climb over-rotated the crosshair upward.
            // Do not inject a correction until bullet-direction truth is measured.
            state.RecoilCompensation=0f;
            // Native recoil remains the sole recoil controller; no extra camera curve.
            var spot=AimMath.Ray(eyeVec,state.Output.Angles);
            _modelSpots[pointer]=spot;
            WriteModelSpot(bot,spot);
            RefreshFireGuard(controller);
        }
        catch(Exception error)
        {
            if(controller != null)ReleaseHuman(controller.Slot);
            _humanSettings.Enabled=false;ResetHuman();
            Logger.LogError(error,"Human aim disabled after runtime error; previous aiming behavior restored");
        }
        return HookResult.Continue;
    }
    // Dense fallback candidates used only when every preferred aim point is
    // occluded: partial cover often leaves a shoulder, arm, hip or leg exposed.
    private static readonly (float Frac,float Lateral)[] _fallbackPoints=BuildFallbackPoints();
    private static (float Frac,float Lateral)[] BuildFallbackPoints()
    {
        var points=new List<(float,float)>();
        foreach(var frac in new[]{.95f,.85f,.82f,.7f,.55f})
            foreach(var lateral in new[]{0f,5f,-5f,10f,-10f})
                points.Add((frac,lateral));
        return points.ToArray();
    }
    // Long enough to absorb a normal peek/cover flicker, short enough that a
    // genuinely new engagement still starts a fresh perception/motor plan.
    private const float ReacquireResetSeconds=1.0f;
    private bool TrySelectHumanPoint(CCSBot bot,CCSPlayerPawn pawn,CCSPlayerPawn enemy,Vector eye,out Vec point,out float radius,out string result,out int attempts,out string pointLabel,out float rayFraction,out bool smokeBlocked)
    {
        point=default;radius=4;result="no-point";attempts=0;pointLabel="none";rayFraction=0f;smokeBlocked=false;
        var weapon=pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName;
        var order=_aimMode switch
        {
            AimMode.HEAD => weapon=="weapon_awp" ? _priorityTrunk : _priorityHead,
            AimMode.BODY => _priorityBody,
            // Mixed: every weapon except AWP uses the head-first order.
            _ => weapon != null && _bodyFirstWeapons.Contains(weapon) ? _priorityTrunk : _priorityHead
        };
        var forward=new Angles(pawn.EyeAngles.X,pawn.EyeAngles.Y);
        float fovHalf=_humanSettings.HorizontalFieldOfView*.5f;
        int attemptsLocal=0,fovFail=0,rayFail=0,smokeFail=0;
        float bestFraction=0f;
        Vec chosen=default;float chosenRadius=4;string chosenLabel="none";bool found=false;
        bool Consider(float x,float y,float z,float candidateRadius,string label,float minFraction)
        {
            attemptsLocal++;
            var candidate=new Vec(x,y,z);
            var angle=AimMath.Angles(new(eye.X,eye.Y,eye.Z),candidate);
            var delta=AimMath.Delta(angle,forward);
            if(MathF.Abs(delta.Y)>fovHalf || MathF.Abs(delta.X)>50){fovFail++;return false;}
            // Smoke is the cheapest rejection and does not depend on an engine trace.
            if(HumanSmokeBlocks(eye,candidate)){smokeFail++;return false;}
            if(!PointVisibleFromEye(eye,x,y,z,minFraction,out var fraction)){rayFail++;bestFraction=MathF.Max(bestFraction,fraction);return false;}
            chosen=candidate;chosenRadius=candidateRadius;chosenLabel=label;bestFraction=MathF.Max(bestFraction,fraction);found=true;
            return true;
        }
        foreach(var index in order)
        {
            if(!TryComputePartPos(enemy,index,out var x,out var y,out var z))continue;
            var candidateRadius=index is >=3 and <=7 or 10 or 11 ? 8f : 4f;
            if(Consider(x,y,z,candidateRadius,"base:"+_aimPoints[index].Name,.999f))break;
        }
        if(!found)
            foreach(var (frac,lateral) in _fallbackPoints)
            {
                if(!TryComputeOffsetPos(enemy,frac,lateral,out var x,out var y,out var z))continue;
                var candidateRadius=frac>=.9f?4f:frac>=.55f?8f:6f;
                var minFraction=frac>=.9f?.999f:.985f;
                if(Consider(x,y,z,candidateRadius,$"fan:{frac:0.00}/{lateral:0}",minFraction))break;
            }
        attempts=attemptsLocal;
        rayFraction=bestFraction;
        if(found)
        {
            point=chosen;radius=chosenRadius;pointLabel=chosenLabel;result="ok:"+chosenLabel;
            return true;
        }
        result=rayFail>0?$"ray({rayFail}/{attemptsLocal})":fovFail>0?$"fov({fovFail}/{attemptsLocal})":smokeFail>0?$"smoke({smokeFail}/{attemptsLocal})":"no-point";
        smokeBlocked=smokeFail>0;
        pointLabel="none";
        return false;
    }
    // Coarse body-level sample for a natively visible target hidden by smoke.
    // The error is deliberately large and derived from the smoke geometry, so the
    // model plans and fires from an imprecise belief instead of perfect native aim.
    private static bool TryBuildSmokeSample(Vec eye,CCSPlayerPawn enemy,out Vec point,out float radius)
    {
        point=default;radius=14;
        var origin=enemy.AbsOrigin;
        if(origin==null)return false;
        float eyeZ=enemy.ViewOffset?.Z ?? 64f;
        var body=new Vec(origin.X,origin.Y,origin.Z+eyeZ*.6f);
        float distance=Vec.Distance(eye,body);
        float error=SmokeAimPolicy.Error(distance);
        float angle=Random.Shared.NextSingle()*MathF.Tau;
        point=new Vec(body.X+MathF.Cos(angle)*error,body.Y+MathF.Sin(angle)*error,
            body.Z+(Random.Shared.NextSingle()*2f-1f)*error*.35f);
        return float.IsFinite(point.X+point.Y+point.Z);
    }
    private static void WriteModelSpot(CCSBot bot,Vec point)
    {
        var spot=bot.TargetSpot;spot.X=point.X;spot.Y=point.Y;spot.Z=point.Z;
        var predicted=bot.TargetSpotPredicted;predicted.X=point.X;predicted.Y=point.Y;predicted.Z=point.Z;
        var velocity=bot.TargetSpotVelocity;velocity.X=0;velocity.Y=0;velocity.Z=0;
    }
    private bool WriteCachedHumanSpot(CCSBot bot,CCSPlayerController controller,Vector eye)
    {
        if(!HumanActive || controller.HasBeenControlledByPlayerThisRound || OtherViewOwner(controller.Slot))return false;
        // PickNewAimSpot cannot restore perfect coordinates between model updates.
        if(_modelSpots.TryGetValue(bot.Handle,out var point))WriteModelSpot(bot,point);
        // No active visual plan: leave native searching intact, but never run legacy perfect aim override.
        return true;
    }
    private bool HumanSmokeBlocks(Vector eye,Vec target)
    {
        RefreshSmokeVolumes(Server.CurrentTime);
        var start=new Vec(eye.X,eye.Y,eye.Z);
        return _smokes.Any(s=>SmokeAimPolicy.Blocks(start,target,s.Center,s.Radius));
    }
}
