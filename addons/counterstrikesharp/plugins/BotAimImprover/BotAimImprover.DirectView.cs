using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;
namespace BotAimImprover;
public partial class BotAimImprover
{
    private MemoryFunctionVoid<nint,nint>? _setEyeAngles;
    private void InitializeDirectView(JsonElement data,string platform)
    {
        try
        {
            var signature=data.GetProperty("CCSPlayerPawn::SetEyeAngles").GetProperty("signatures").GetProperty(platform).GetString()!;
            _setEyeAngles=new MemoryFunctionVoid<nint,nint>(signature);
            if(_setEyeAngles.Handle==nint.Zero)throw new InvalidOperationException("SetEyeAngles unavailable");
        }
        catch(Exception error) { _setEyeAngles=null;Logger.LogWarning(error,"Experimental direct view unavailable"); }
    }
    private HookResult OnHumanLookPost(DynamicHook hook)
    {
        if(!HumanActive || !_humanSettings.DirectViewControl || _setEyeAngles==null)return HookResult.Continue;
        CCSPlayerController? controller=null;
        try
        {
            var pointer=hook.GetParam<nint>(0);controller=ResolveBotController(pointer);
            if(controller is not {IsValid:true,IsBot:true,PawnIsAlive:true} || controller.HasBeenControlledByPlayerThisRound
                || OtherViewOwner(controller.Slot) || !_modelSpots.ContainsKey(pointer)
                || !_humanStates.TryGetValue(controller.Slot,out var state) || state.Pawn!=controller.PlayerPawn.Raw
                || state.LastUsed!=Server.CurrentTime || controller.PlayerPawn.Value is not {IsValid:true} pawn)return HookResult.Continue;
            var angles=state.Output.Angles;
            if(!float.IsFinite(angles.X+angles.Y))throw new InvalidOperationException("Invalid planned view");
            var q=new QAngle(angles.X,angles.Y,0);
            // Matches the installed BotController bb19124 native replay helper:
            // temporary FAKECLIENT flag avoids queuing a human UserCmd correction. Never changes pawn ground flags.
            var flags=controller.Flags;
            try
            {
                if(OperatingSystem.IsWindows())controller.Flags=flags|0x100u;
                _setEyeAngles.Invoke(pawn.Handle,q.Handle);
            }
            finally { if(OperatingSystem.IsWindows() && controller.IsValid)controller.Flags=flags; }
            var bot=pawn.Bot;
            if(bot!=null){bot.LookPitch=angles.X;bot.LookYaw=angles.Y;}
        }
        catch(Exception error)
        {
            _humanSettings.DirectViewControl=false;
            Logger.LogError(error,"Experimental direct view disabled");
        }
        return HookResult.Continue;
    }
}
