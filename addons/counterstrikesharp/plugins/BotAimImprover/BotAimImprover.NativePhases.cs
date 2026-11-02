using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using System.Text.Json;
namespace BotAimImprover;

public partial class BotAimImprover
{
    private MemoryFunctionVoid<nint>? _nativeUpdatePhase,_nativeUpkeepPhase;
    private readonly Dictionary<nint,Stack<string>> _nativePhases=new();
    private readonly Dictionary<string,long> _nativePhaseCalls=new();
    private bool _nativePhasesReady;
    private void InitializeNativePhases(JsonElement data,string platform)
    {
        // Both void(bot*) signatures match installed BotController bb19124.
        _nativeUpdatePhase=new(data.GetProperty("CCSBot::Update").GetProperty("signatures").GetProperty(platform).GetString()!);
        _nativeUpkeepPhase=new(data.GetProperty("CCSBot::Upkeep").GetProperty("signatures").GetProperty(platform).GetString()!);
        if(_nativeUpdatePhase.Handle==nint.Zero || _nativeUpkeepPhase.Handle==nint.Zero)throw new InvalidOperationException("Native phase signatures missing");
        _nativeUpdatePhase.Hook(OnNativeUpdatePre,HookMode.Pre);
        _nativeUpdatePhase.Hook(OnNativeUpdatePost,HookMode.Post);
        _nativeUpkeepPhase.Hook(OnNativeUpkeepPre,HookMode.Pre);
        _nativeUpkeepPhase.Hook(OnNativeUpkeepPost,HookMode.Post);
        _nativePhasesReady=true;
    }
    private HookResult EnterNativePhase(DynamicHook hook,string name)
    {
        if(_telemetry==null)return HookResult.Continue;
        _nativePhaseCalls[name]=_nativePhaseCalls.GetValueOrDefault(name)+1;
        var pointer=hook.GetParam<nint>(0);
        if(!_nativePhases.TryGetValue(pointer,out var phases))_nativePhases[pointer]=phases=new();
        phases.Push(name);
        return HookResult.Continue;
    }
    private HookResult ExitNativePhase(DynamicHook hook)
    {
        var pointer=hook.GetParam<nint>(0);
        if(_nativePhases.TryGetValue(pointer,out var phases))
        {
            if(phases.Count>0)phases.Pop();
            if(phases.Count==0)_nativePhases.Remove(pointer);
        }
        return HookResult.Continue;
    }
    private HookResult OnNativeUpdatePre(DynamicHook hook)
    {
        if(HumanActive)PrepareHumanAim(hook.GetParam<nint>(0));
        RefreshNativeFireGuard(hook);
        return EnterNativePhase(hook,"update");
    }
    private HookResult OnNativeUpdatePost(DynamicHook hook)
    {
        RefreshNativeFireGuard(hook);
        return ExitNativePhase(hook);
    }
    private HookResult OnNativeUpkeepPre(DynamicHook hook)=>EnterNativePhase(hook,"upkeep");
    private HookResult OnNativeUpkeepPost(DynamicHook hook)=>ExitNativePhase(hook);
    private string NativePhase(nint pointer)=>!_nativePhasesReady ? "diagnostics-unavailable"
        : _nativePhases.TryGetValue(pointer,out var phases) && phases.TryPeek(out var phase) ? phase : "outside-native-phase";
    private void UninstallNativePhases()
    {
        _nativePhasesReady=false;
        try{_nativeUpdatePhase?.Unhook(OnNativeUpdatePre,HookMode.Pre);}catch{}
        try{_nativeUpdatePhase?.Unhook(OnNativeUpdatePost,HookMode.Post);}catch{}
        try{_nativeUpkeepPhase?.Unhook(OnNativeUpkeepPre,HookMode.Pre);}catch{}
        try{_nativeUpkeepPhase?.Unhook(OnNativeUpkeepPost,HookMode.Post);}catch{}
        _nativePhases.Clear();
    }
}
