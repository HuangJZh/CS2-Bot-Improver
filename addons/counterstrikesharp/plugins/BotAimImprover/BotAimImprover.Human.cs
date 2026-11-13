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
        public bool NativeCombat {get;set;}=false;
        public bool Diagnostics {get;set;}=false;
        public bool DirectViewControl {get;set;}=false; // Experimental until recoil/weapon checks are validated.
        public bool LargeTurnAssist {get;set;}=true;
        public float HorizontalFieldOfView {get;set;}=110;
        public float HumanAllyStrength {get;set;}=CombatBalancePolicy.DefaultAllyStrength;
        public float EnemyStrength {get;set;}=CombatBalancePolicy.DefaultEnemyStrength;
        public bool EnemyElites {get;set;}=true;
        public bool TeamNavigation {get;set;}=true;
        public bool RoundLearning {get;set;}=true;
        public bool TeamSupport {get;set;}=true;
        public bool RetakeAI {get;set;}=true;
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
        public CombatHistory Combat=new();
        public float UnderFireUntil {get=>Combat.UnderFireUntil;set=>Combat.UnderFireUntil=value;}
        public float? FirstVisualAt;
        public float? PlanHoldSince;
        public bool PlanHoldTimedOut;
        public string LastSampleResult="none";
        public int LastSampleAttempts;
        public string LastSamplePoint="none";
        public float? LastSampleRayFraction;
        public float AllowLatchUntil;
        public float LastAimFeedbackCorrection;
        public float LastShotAt=>Combat.LastShotAt;
        public int SprayShots=>Combat.SprayShots;
        public float SprayBasePitchError {get=>Combat.SprayBasePitchError;set=>Combat.SprayBasePitchError=value;}
        public float RecoilCompensation;
        public float BurstCooldownUntil=>Combat.BurstCooldownUntil;
        public BurstCadence Cadence=>Combat.Cadence;
        public AimExecutionController Execution=new();
        public ExecutionRequest ExecutionRequest;
        public ExecutionPermission ExecutionPermission;
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
        InitializeTactics();
        InitializeTeamNavigation();
        InitializeRoundLearning();
        InitializeTeamSupport();
        InitializeRetakeAi();
        RegisterListener<Listeners.OnMapStart>(_=>Server.NextFrame(ApplyTeammatePassthrough));
        RegisterEventHandler<EventRoundStart>((e,i)=>{ApplyTeammatePassthrough();return HookResult.Continue;});
        ApplyTeammatePassthrough();
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
            MaintainRetakePulses();
            if(RetakeOwnsGoals&&(!HumanActive||_humanSettings.NativeCombat||!_humanSettings.RetakeAI))EndRetakeAi(true);
            CleanCombatHistories();
            // ReleaseHuman removes entries but never adds to this dictionary.
            foreach(var (slot,state) in _humanStates)
            {
                var p=EntityPlayerAtSlot(slot);
                if(p is not {IsValid:true,IsBot:true,PawnIsAlive:true} || p.HasBeenControlledByPlayerThisRound
                    || p.PlayerPawn.Raw!=state.Pawn || Server.CurrentTime-state.LastUsed>.5f)ReleaseHuman(slot);
            }
            MaintainFireGuards();
            MaintainTacticalMovement();
            TickTeamNavigation();
            TickTeamSupport();
        });
        RegisterEventHandler<EventPlayerDeath>((e,i) => { if(e.Userid is {} p){ForgetVisualTarget(p.PlayerPawn.Raw);CancelSoundAttention(p);ReleaseHuman(p.Slot);ReleaseFireGuard(p.Slot);_attentionTurnAt.Remove(p.Slot);ResetSilentMovement(p.Slot);}return HookResult.Continue; });
        // Track bursts so the aim output can model human recoil control after the
        // first few rounds instead of holding a perfectly steady target point.
        RegisterEventHandler<EventWeaponFire>((e,i) => {
            string eventItem=e.Weapon ?? "";
            if(!eventItem.StartsWith("weapon_"))eventItem="weapon_"+eventItem;
            if(HumanFireContext.Classify(eventItem)==AimWeaponClass.Other)return HookResult.Continue;
            if(e.Userid is {IsValid:true,IsBot:true,PawnIsAlive:true} shooter
                && !shooter.HasBeenControlledByPlayerThisRound && shooter.PlayerPawn.Value is {IsValid:true} firePawn)
            {
                float fireNow=Server.CurrentTime;
                var history=HistoryFor(shooter,firePawn);
                var profile=CombatProfile(shooter,fireNow);
                history.Shot(fireNow,firePawn.Bot?.Enemy.Raw??uint.MaxValue,profile.BurstLength,
                    profile.BurstPause*(.8f+Random.Shared.NextSingle()*.4f));
                if(firePawn.Bot is {} firingBot){_turnAssistActive.Remove(firingBot.Handle);_pendingLargeTurns.Remove(firingBot.Handle);}
                if(_humanStates.TryGetValue(shooter.Slot,out var fireState)&&fireState.Pawn==shooter.PlayerPawn.Raw
                    &&fireState.PlannedWeapon==(firePawn.WeaponServices?.ActiveWeapon.Raw??uint.MaxValue)
                    &&fireState.Enemy==history.LastTarget)
                {
                    fireState.Model.CommitShot(fireNow,new(firePawn.EyeAngles.X,firePawn.EyeAngles.Y));
                    fireState.AllowLatchUntil=fireNow+.15f;
                    fireState.Execution.Shot();
                    if(fireState.SprayShots==1)fireState.SprayBasePitchError=firePawn.EyeAngles.X-fireState.Model.LastDesiredAngles.X;
                }
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
            Server.PrintToConsole($"[HumanAim] active={HumanActive} difficulty={_humanDifficulty} direct_requested={_humanSettings.DirectViewControl} direct_available={_setEyeAngles!=null} native_ready={_nativePhasesReady} guards={_fireGuards.Count} pending={_fireGuards.Values.Count(g=>g.Token<=0)} calls={_humanLookCalls} bots={EntityPlayers().Count(p=>p.IsBot)} alive={EntityPlayers().Count(p=>p.PawnIsAlive)} states={_humanStates.Count} phases={JsonSerializer.Serialize(_humanPhases)}");
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
            _humanSettings.HumanAllyStrength=CombatBalancePolicy.NormalizeAllyStrength(_humanSettings.HumanAllyStrength);
            _humanSettings.EnemyStrength=CombatBalancePolicy.NormalizeEnemyStrength(_humanSettings.EnemyStrength);
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
        CancelCoverPeek(slot,"visual-context-changed");
        if(!_humanStates.Remove(slot,out var state))return;
        _modelSpots.Remove(state.Pointer);
    }
    private void ResetHuman()
    {
        ClearRetakeFireRecovery();
        _testGateUntil=float.NegativeInfinity;
        foreach(var slot in _humanStates.Keys.ToArray())ReleaseHuman(slot);
        foreach(var slot in _fireGuards.Keys.ToArray())ReleaseFireGuard(slot);
        foreach(var slot in _weaponTurnClocks.Keys.ToArray())ReleaseWeaponTurnClock(slot);
        _modelSpots.Clear();_humanPhases.Clear();_soundPreaim.Clear();_lastFootstep.Clear();_attentionTurnAt.Clear();
        _preaimTargets.Clear();_preaimVisited.Clear();ResetAllSilentMovement();ReadHumanDifficulty();
        ResetTactics();_aimIntents.Clear();_humanTeamsScanAt=0;_humanTeams=0;
        _eliteProfiles.Clear();
        _combatHistories.Clear();_noticedTargets.Clear();_nativeTurnAt.Clear();_turnAssistActive.Clear();_pendingLargeTurns.Clear();ResetTeamSupport();
        _visualAttention.Clear();_visibilityScans.Clear();_soundFocus.Clear();_soundTurnOwners.Clear();
        _nativeContacts.Clear();
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
        if(HasReliableVisibleThreat(controller,now)){CancelSoundAttention(controller);return false;}
        if(!_soundFocus.TryGetValue(controller.Slot,out var owner)||owner.Pawn!=controller.PlayerPawn.Raw)
            _soundFocus[controller.Slot]=owner=new(controller.PlayerPawn.Raw,new());
        uint? previousSource=owner.Focus.Cue?.SourcePawn;
        if(_soundPreaim.TryGetValue(controller.Slot,out var incoming))owner.Focus.Select(incoming,now);
        if(!owner.Focus.Holding(now))
        {
            owner.Focus.End(now);CancelSoundSteering(controller);
            if(incoming!=null&&incoming.Until<=now)_soundPreaim.Remove(controller.Slot);
            return false;
        }
        var aim=owner.Focus.Cue!;
        if(previousSource.HasValue&&previousSource.Value!=aim.SourcePawn)CancelSoundSteering(controller);
        var current=new Angles(pawn.EyeAngles.X,pawn.EyeAngles.Y);
        var desired=owner.Focus.Ready(now)?AimMath.Angles(eyeVec,aim.Position):current;
        var profile=CombatProfile(controller,now);
        var next=AimMath.Advance(current,desired,AttentionDelta(controller.Slot,now),profile.TrackingTime,profile.MaxSpeed);
        var spot=AimMath.Ray(eyeVec,next);
        _modelSpots[pointer]=spot;
        WriteModelSpot(bot,spot);
        // Synchronize bounded native look steering as well as TargetSpot: sound
        // search has no visual Enemy, so TargetSpot alone need not turn the view.
        _soundTurnOwners.Add(pointer);
        WriteNativeTurnGoal(bot,current,desired,profile,now,false,true);
        RefreshFireGuard(controller);
        return true;
    }
    private HookResult OnHumanLook(DynamicHook hook)
    {
        _humanLookCalls++;
        PrepareHumanAim(hook.GetParam<nint>(0));
        return HookResult.Continue;
    }
    // Also called at target-spot selection, before the native AI can submit its
    // first attack. Idempotent model/execution clocks prevent double advancement.
    private HookResult PrepareHumanAim(nint pointer)
    {
        if(!HumanActive)return HookResult.Continue;
        if(_humanSettings.NativeCombat){ObserveNativeCombat(pointer);return HookResult.Continue;}
        CCSPlayerController? controller=null;
        try
        {
            controller=ResolveBotController(pointer);
            if(controller is not {IsValid:true,IsBot:true,PawnIsAlive:true} || controller.HasBeenControlledByPlayerThisRound)return HookResult.Continue;
            var pawn=controller.PlayerPawn.Value;
            if(pawn is {IsValid:true})RecoverRetakeEquipment(controller,pawn);
            if(pawn is not {IsValid:true} || !IsGun(pawn) || OtherViewOwner(controller.Slot))
            { CancelSoundAttention(controller);ReleaseHuman(controller.Slot);ReleaseFireGuard(controller.Slot);_modelSpots.Remove(pointer);return HookResult.Continue; }
            if(!TryGetBotEyePosition(controller,out var eye))return HookResult.Continue;
            var eyeVec=new Vec(eye.X,eye.Y,eye.Z);
            float now=Server.CurrentTime;
            var bot=new CCSBot(pointer);
            if(pawn.IsDefusing||(_plantingUntil.TryGetValue(controller.Slot,out var plantingUntil)&&now<plantingUntil))
            {CancelSoundAttention(controller);ResetSilentMovement(controller.Slot);ReleaseHuman(controller.Slot);ReleaseFireGuard(controller.Slot);return HookResult.Continue;}
            bool retake=IsRetakeBot(controller);
            if(!retake)TickSilentMovement(controller,pawn,bot,now);
            bool visualPriority=retake?UpdateRetakeSight(controller,pawn,bot,eye,now):HasReliableVisibleThreat(controller,now);
            if(visualPriority)CancelSoundAttention(controller);
            if(bot.Enemy.Value is not {IsValid:true} enemy || enemy.LifeState!=0 || enemy.TeamNum==controller.TeamNum)
            {
                // The native enemy handle can flicker between peeks. Keep the memory
                // based plan for a short grace period instead of resetting every
                // model (reset means a fresh sensor delay and reaction before the
                // next shot). No target spot is written while the target is gone.
                if(_humanStates.TryGetValue(controller.Slot,out var held) && held.Pawn==controller.PlayerPawn.Raw
                    && now-held.LastUsed<.35f && held.Model.HasObservation && now-held.Model.LastVisible<.22f)
                {
                    held.LastUsed=now;
                    held.Output=held.Model.Step(now,eyeVec,false,default,new(pawn.EyeAngles.X,pawn.EyeAngles.Y));
                    var memorySpot=AimMath.Ray(eyeVec,held.Output.Angles);_modelSpots[pointer]=memorySpot;WriteModelSpot(bot,memorySpot);
                    RefreshFireGuard(controller);
                    return HookResult.Continue;
                }
                if(!visualPriority&&!retake)
                {
                    if(TryApplySoundPreaim(controller,bot,pointer,pawn,eyeVec,now))return HookResult.Continue;
                    if(TryApplySupportPreaim(controller,bot,pointer,pawn,eyeVec,now))return HookResult.Continue;
                    if(TryApplyMapPreaim(controller,bot,pointer,pawn,eyeVec,now))return HookResult.Continue;
                }
                ReleaseHuman(controller.Slot);_modelSpots.Remove(pointer);RefreshFireGuard(controller);return HookResult.Continue;
            }
            _preaimTargets.Remove(controller.Slot);
            CancelCoverPeek(controller.Slot,"enemy-context");
            if(visualPriority&&_visualAttention.TryGetValue(controller.Slot,out var protectedSight)
                &&protectedSight.Target!=enemy.EntityHandle.Raw&&now<protectedSight.Until)
            {ReleaseHuman(controller.Slot);_modelSpots.Remove(pointer);RefreshFireGuard(controller);return HookResult.Continue;}
            var combatProfile=CombatProfile(controller,now);
            NoticeAlternativeTargets(controller,pawn,eye,now);
            if(!_humanStates.TryGetValue(controller.Slot,out var state) || state.Pawn!=controller.PlayerPawn.Raw || state.Enemy!=enemy.EntityHandle.Raw
                ||state.Model.Profile!=combatProfile
                ||state.PlannedWeapon!=(pawn.WeaponServices?.ActiveWeapon.Raw??uint.MaxValue))
            {
                ReleaseHuman(controller.Slot);
                state=CreateHumanState(controller,pawn,enemy,pointer,combatProfile,now);
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
                visible=sensorGate && TrySelectHumanPoint(controller,bot,pawn,enemy,eye,out sample,out sampleRadius,out sampleResult,out sampleAttempts,out samplePoint,out sampleRayFraction,out smokeBlocked);
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
                    // Preferred/fallback samples also confirm partial exposures
                    // such as HEAD_TOP; the fast head/chest scan is not exhaustive.
                    if(!degradedSmoke)ConfirmVisualAttention(controller,enemy.EntityHandle.Raw,now);
                    // Reacquisition after lost memory is a fresh movement only when the
                    // target was gone long enough that the old plan is no longer usable.
                    // Short peeks keep the plan so reacquiring does not pay a fresh
                    // sensor delay + reaction + flick every time.
                    float visibleGap=state.Model.HasObservation ? now-state.Model.LastVisible : float.PositiveInfinity;
                    if(state.Model.HasObservation && visibleGap>ReacquireResetSeconds)
                    {
                        WriteTelemetry(new {kind="model_lifecycle",tick=Server.TickCount,time=now,slot=controller.Slot,action="reacquire-reset",gap=visibleGap});
                        ReleaseHuman(controller.Slot);
                        state=CreateHumanState(controller,pawn,enemy,pointer,combatProfile,now);
                        state.LastUsed=now;state.LastSight=true;state.NextSensorCheck=now+combatProfile.SampleInterval;
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
            if(visible){ApplyVisibleAlertCredit(controller,state,now);CancelSoundAttention(controller);}
            else if(!retake&&TryApplySoundPreaim(controller,bot,pointer,pawn,eyeVec,now))return HookResult.Continue;
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
            // Close the native execution gap before the first shot. During a live
            // spray native recoil owns the view and this bounded correction fades.
            var actualView=new Angles(pawn.EyeAngles.X,pawn.EyeAngles.Y);
            var viewDelta=AimMath.Delta(state.Model.LastDesiredAngles,actualView);
            state.LastAimFeedbackCorrection=viewDelta.Length();
            // Recoil-control measurement kept for calibration, but the first
            // compensation attempt (8.13) reduced hits from .48 to .21 at 600 u:
            // the engine view already reflects native downward compensation, so
            // correcting the observed climb over-rotated the crosshair upward.
            // Do not inject a correction until bullet-direction truth is measured.
            state.RecoilCompensation=0f;
            bool settle=visible && FirstShotPending(state,now) && now-state.LastShotAt>.35f
                &&state.Output.Phase is "flick" or "tracking" or "correction";
            float gain=_humanDifficulty switch {"Low"=>.6f,"High"=>1f,_=>.8f};
            state.ExecutionRequest=state.Execution.Update(now,state.Output.Angles,actualView,settle,gain);
            var spot=AimMath.Ray(eyeVec,state.ExecutionRequest.Angles);
            _modelSpots[pointer]=spot;
            WriteModelSpot(bot,spot);
            bool liveSpray=now-state.LastShotAt<.2f;
            if(!liveSpray&&visible&&state.Output.Phase=="flick"&&(state.Model.ContactGapDegrees>35||_turnAssistActive.Contains(pointer)))
                WriteNativeTurnGoal(bot,actualView,state.Model.MovementEndpoint,state.Model.Profile,now);
            else if(!liveSpray&&visible&&state.Output.Phase is "tracking" or "correction"
                &&(MathF.Abs(AimMath.Wrap(state.Output.Angles.Y-actualView.Y))>6||_turnAssistActive.Contains(pointer)))
                WriteNativeTurnGoal(bot,actualView,state.Output.Angles,state.Model.Profile,now);
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
    private bool TrySelectHumanPoint(CCSPlayerController controller,CCSBot bot,CCSPlayerPawn pawn,CCSPlayerPawn enemy,Vector eye,out Vec point,out float radius,out string result,out int attempts,out string pointLabel,out float rayFraction,out bool smokeBlocked)
    {
        point=default;radius=4;result="no-point";attempts=0;pointLabel="none";rayFraction=0f;smokeBlocked=false;
        if(!TryReadTargetGeometry(enemy,out var geometry))return false;
        var order=AimOrder(controller,pawn,enemy);
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
            if(!TryComputePartPos(geometry,index,out var x,out var y,out var z))continue;
            var candidateRadius=index is >=3 and <=7 or 10 or 11 ? 8f : 4f;
            if(Consider(x,y,z,candidateRadius,"base:"+_aimPoints[index].Name,.999f))break;
        }
        if(!found)
            foreach(var (frac,lateral) in _fallbackPoints)
            {
                if(!geometry.TryPoint(frac,lateral,false,out var x,out var y,out var z))continue;
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
    private readonly Dictionary<nint,float> _nativeTurnAt=new();
    private sealed record PendingLargeTurn(uint Pawn,Angles Before,Angles Next,Angles Goal,float At,bool Auditory=false);
    private readonly Dictionary<nint,PendingLargeTurn> _pendingLargeTurns=new();
    private readonly HashSet<nint> _turnAssistActive=new();
    // Large turns alone may take a bounded view step. Small aim/recoil correction
    // remains native; no full 90/180-degree endpoint is applied in one callback.
    private void WriteNativeTurnGoal(CCSBot bot,Angles actual,Angles goal,HumanAimProfile profile,float now,bool retainForVisual=false,bool soundOwner=false)
    {
        bool seen=_nativeTurnAt.TryGetValue(bot.Handle,out var last);
        if(seen&&now==last)
        {
            if(soundOwner&&_pendingLargeTurns.TryGetValue(bot.Handle,out var repeat)&&repeat.Auditory&&repeat.At==now)
            {bot.LookPitch=repeat.Next.X;bot.LookYaw=repeat.Next.Y;}
            return;
        }
        float dt=seen?Math.Clamp(now-last,0,.02f):Server.TickInterval;
        _nativeTurnAt[bot.Handle]=now;
        float speed=HumanAimModel.TurnSpeed(profile.MaxSpeed,AimMath.Delta(goal,actual).Length());
        var next=AimMath.Advance(actual,goal,dt,.012f/HumanAimModel.TurnRateMultiplier,speed);
        bot.LookPitch=next.X;bot.LookYaw=next.Y;
        float gap=MathF.Abs(AimMath.Delta(goal,actual).Y);
        if(gap>35)_turnAssistActive.Add(bot.Handle);
        if(gap<=.4f&&!retainForVisual)_turnAssistActive.Remove(bot.Handle);
        if((soundOwner||_humanSettings.LargeTurnAssist&&!_humanSettings.DirectViewControl&&_setEyeAngles!=null&&_turnAssistActive.Contains(bot.Handle))
            &&ResolveBotController(bot.Handle) is {IsValid:true,IsBot:true,PawnIsAlive:true} p
            &&!p.HasBeenControlledByPlayerThisRound&&!OtherViewOwner(p.Slot)&&p.PlayerPawn.Value is {IsValid:true} pawn)
            _pendingLargeTurns[bot.Handle]=new(p.PlayerPawn.Raw,actual,next,goal,now,soundOwner);
    }
    private void ApplyPendingLargeTurn(nint pointer)
    {
        if(!_pendingLargeTurns.TryGetValue(pointer,out var turn)||turn.At!=Server.CurrentTime
            ||ResolveBotController(pointer) is not {IsValid:true,IsBot:true,PawnIsAlive:true} p
            ||p.PlayerPawn.Raw!=turn.Pawn||p.HasBeenControlledByPlayerThisRound||OtherViewOwner(p.Slot)
            ||p.PlayerPawn.Value is not {IsValid:true} pawn||!IsGun(pawn))return;
        if(pawn.IsDefusing||(_plantingUntil.TryGetValue(p.Slot,out var planting)&&Server.CurrentTime<planting))
        {CancelSoundAttention(p);_pendingLargeTurns.Remove(pointer);return;}
        if(!turn.Auditory)_pendingLargeTurns.Remove(pointer);
        else
        {
            if(HasReliableVisibleThreat(p,Server.CurrentTime)){CancelSoundAttention(p);return;}
            if(!_soundFocus.TryGetValue(p.Slot,out var focus)||focus.Pawn!=p.PlayerPawn.Raw||!focus.Focus.Holding(Server.CurrentTime))
            {CancelSoundSteering(p);return;}
        }
        uint flags=p.Flags;
        try
        {
            try
            {
                if(_setEyeAngles!=null&&(!turn.Auditory||_humanSettings.LargeTurnAssist))
                {
                    if(OperatingSystem.IsWindows())p.Flags=flags|0x100u;
                    _setEyeAngles.Invoke(pawn.Handle,new QAngle(turn.Next.X,turn.Next.Y,0).Handle);
                }
                var bot=new CCSBot(pointer);
                bot.LookPitch=turn.Next.X;bot.LookYaw=turn.Next.Y;bot.LookPitchVel=0;bot.LookYawVel=0;
            }
            finally{if(OperatingSystem.IsWindows()&&p.IsValid)p.Flags=flags;}
            WriteTelemetry(new{kind=turn.Auditory?"sound_gaze_step":"large_turn_step",time=Server.CurrentTime,slot=p.Slot,pawn=turn.Pawn,
                source_pawn=turn.Auditory?_soundFocus.GetValueOrDefault(p.Slot)?.Focus.Cue?.SourcePawn:null,
                before_yaw=turn.Before.Y,requested_yaw=turn.Next.Y,goal_yaw=turn.Goal.Y});
        }
        catch(Exception error)
        {
            _humanSettings.LargeTurnAssist=false;_pendingLargeTurns.Clear();_turnAssistActive.Clear();
            Logger.LogError(error,"Bounded turn assistance disabled; native turn and facing fire safety remain active");
        }
    }
    private bool WriteCachedHumanSpot(CCSBot bot,CCSPlayerController controller,Vector eye)
    {
        if(!HumanActive || _humanSettings.NativeCombat || controller.HasBeenControlledByPlayerThisRound || OtherViewOwner(controller.Slot))return false;
        // PickNewAimSpot cannot restore perfect coordinates between model updates.
        if(_modelSpots.TryGetValue(bot.Handle,out var point))WriteModelSpot(bot,point);
        // No active visual plan: leave native searching intact, but never run legacy perfect aim override.
        return true;
    }
    private bool HumanSmokeBlocks(Vector eye,Vec target)
    {
        RefreshSmokeVolumes(Server.CurrentTime);
        var start=new Vec(eye.X,eye.Y,eye.Z);
        foreach(var s in _smokes)
            if(SmokeAimPolicy.Blocks(start,target,s.Center,s.Radius))return true;
        return false;
    }
}
