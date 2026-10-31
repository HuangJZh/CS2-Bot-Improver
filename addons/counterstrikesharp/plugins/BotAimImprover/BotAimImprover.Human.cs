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
        RegisterEventHandler<EventPlayerDeath>((e,i) => { if(e.Userid is {} p){ReleaseHuman(p.Slot);ReleaseFireGuard(p.Slot);}return HookResult.Continue; });
        RegisterEventHandler<EventPlayerHurt>((e,i) => {
            if(e.Userid is {IsValid:true,IsBot:true} victim && e.Attacker is {IsValid:true} attacker && attacker.TeamNum!=victim.TeamNum
                && _humanStates.TryGetValue(victim.Slot,out var state) && state.Pawn==victim.PlayerPawn.Raw)
                state.UnderFireUntil=Server.CurrentTime+.6f;
            return HookResult.Continue;
        });
        RegisterEventHandler<EventPlayerDisconnect>((e,i) => { if(e.Userid is {} p){ReleaseHuman(p.Slot);ReleaseFireGuard(p.Slot);}return HookResult.Continue; });
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
            if(caller != null)return; // Local server console only.
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
        if(!_humanStates.Remove(slot,out var state))return;
        _modelSpots.Remove(state.Pointer);
    }
    private void ResetHuman()
    {
        _testGateUntil=float.NegativeInfinity;
        foreach(var slot in _humanStates.Keys.ToArray())ReleaseHuman(slot);
        foreach(var slot in _fireGuards.Keys.ToArray())ReleaseFireGuard(slot);
        _modelSpots.Clear();_humanPhases.Clear();ReadHumanDifficulty();
    }
    private static bool IsGun(CCSPlayerPawn pawn)
        => pawn.WeaponServices?.ActiveWeapon.Value?.As<CCSWeaponBase>().VData?.GearSlot is gear_slot_t.GEAR_SLOT_RIFLE or gear_slot_t.GEAR_SLOT_PISTOL;
    private bool OtherViewOwner(int slot) => _humanController!.IsLocked(slot,LockKind.Aim)
        || _humanController.IsLocked(slot,LockKind.All) || _humanController.IsReplaying(slot);

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
            var bot=new CCSBot(pointer);
            if(bot.Enemy.Value is not {IsValid:true} enemy || enemy.LifeState!=0 || enemy.TeamNum==controller.TeamNum)
            { ReleaseHuman(controller.Slot);_modelSpots.Remove(pointer);RefreshFireGuard(controller);return HookResult.Continue; }
            if(!TryGetBotEyePosition(controller,out var eye))return HookResult.Continue;
            var eyeVec=new Vec(eye.X,eye.Y,eye.Z);
            float now=Server.CurrentTime;
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
                visible=bot.IsEnemyVisible && pawn.BlindUntilTime<=now && TrySelectHumanPoint(bot,pawn,enemy,eye,out sample,out sampleRadius);
                state.LastSight=visible;
                if(visible)
                {
                    // Reacquisition after lost memory is a fresh movement.
                    if(state.Model.HasObservation && now-state.Model.LastVisible>.35f)
                    {
                        ReleaseHuman(controller.Slot);
                        state=new HumanState{Pointer=pointer,Pawn=controller.PlayerPawn.Raw,Enemy=enemy.EntityHandle.Raw,
                            PlannedWeapon=pawn.WeaponServices?.ActiveWeapon.Raw ?? uint.MaxValue,
                            Model=new(HumanAimProfile.For(_humanDifficulty),new(pawn.EyeAngles.X,pawn.EyeAngles.Y),now,Random.Shared.Next()),
                            LastUsed=now,LastSight=true,NextSensorCheck=now+HumanAimProfile.For(_humanDifficulty).SampleInterval,
                            UnderFireUntil=state.UnderFireUntil};
                        _humanStates[controller.Slot]=state;
                    }
                    // The sample, not the live entity, enters the model's delay queue.
                    state.LastVisualEstimate=state.Model.ObserveVisual(now,eyeVec,sample,new(pawn.EyeAngles.X,pawn.EyeAngles.Y),sampleRadius);
                    if(state.LastVisualEstimate.HasValue)state.FirstVisualAt??=now;
                }
            }
            // Preserve native search/navigation without permitting hidden-target fire.
            if((!visible && !state.Model.HasObservation) || (!visible && now-state.Model.LastVisible>.35f))
            {
                _modelSpots.Remove(pointer);
                state.Output=state.Model.Step(now,eyeVec,false);
                RefreshFireGuard(controller);
                return HookResult.Continue;
            }
            var fireContext=new HumanFireContext(HumanFireContext.Classify(pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName),now<state.UnderFireUntil);
            state.PlannedWeapon=pawn.WeaponServices?.ActiveWeapon.Raw ?? uint.MaxValue;
            state.Output=state.Model.Step(now,eyeVec,visible,fireContext);
            _humanPhases[state.Output.Phase]=_humanPhases.GetValueOrDefault(state.Output.Phase)+1;
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
    private bool TrySelectHumanPoint(CCSBot bot,CCSPlayerPawn pawn,CCSPlayerPawn enemy,Vector eye,out Vec point,out float radius)
    {
        point=default;radius=4;
        var weapon=pawn.WeaponServices?.ActiveWeapon.Value?.DesignerName;
        var order=_aimMode switch
        {
            AimMode.HEAD => weapon=="weapon_awp" ? _priorityTrunk : _priorityHead,
            AimMode.BODY => _priorityBody,
            _ => weapon != null && _bodyFirstWeapons.Contains(weapon) ? _priorityTrunk : _priorityJaw
        };
        var forward=new Angles(pawn.EyeAngles.X,pawn.EyeAngles.Y);
        foreach(var index in order)
        {
            if(!TryComputePartPos(enemy,index,out var x,out var y,out var z))continue;
            var candidate=new Vec(x,y,z);
            var angle=AimMath.Angles(new(eye.X,eye.Y,eye.Z),candidate);
            if(MathF.Abs(AimMath.Delta(angle,forward).Y)>_humanSettings.HorizontalFieldOfView*.5f
                || MathF.Abs(AimMath.Delta(angle,forward).X)>50)continue;
            if(!PointVisibleFromEye(eye,x,y,z) || HumanSmokeBlocks(eye,candidate))continue;
            point=candidate;
            radius=index is >=3 and <=7 or 10 or 11 ? 8 : 4;
            return true;
        }
        return false;
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
