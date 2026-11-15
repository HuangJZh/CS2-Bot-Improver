using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Vec=System.Numerics.Vector3;

namespace BotMovement;

// The existing MoveTo entry writes the requested destination at the displacement
// encoded in this exact signature. CCSBot.GoalPosition is a different AI-state
// goal and may point elsewhere during a local jump. No writes or new hooks here.
public sealed class NativeMoveGoalReader
{
    private int _offset;
    public MemoryFunctionVoid<nint,nint,int>? MoveTo {get;private set;}
    public bool Initialize()
    {
        try
        {
            string signature=OperatingSystem.IsWindows()
                ? "F2 0F 10 02 F2 0F 11 81 E8 02 00 00 8B 42 08 48 8D 91 E0 02 00 00 89 81 F0 02 00 00 44 89 81 F4 02 00 00 E9 ? ? ? ?"
                : "48 8B 06 48 89 87 E0 02 00 00 8B 46 08 48 8D B7 D8 02 00 00 89 97 EC 02 00 00 89 87 E8 02 00 00 E9 ? ? ? ?";
            MoveTo=new(signature);
            if(MoveTo.Handle==0){MoveTo=null;return false;}
            // These offsets are part of the matched instruction bytes above,
            // not a guessed field layout; unknown layouts disable this reader.
            _offset=OperatingSystem.IsWindows()?0x2e8:0x2e0;
            return true;
        }
        catch{MoveTo=null;return false;}
    }
    public bool TryRead(CCSBot bot,out Vec goal)
    {
        goal=default;if(MoveTo==null||bot.Handle==0)return false;
        var value=new Vector(bot.Handle+_offset);goal=new(value.X,value.Y,value.Z);
        return float.IsFinite(goal.X+goal.Y+goal.Z);
    }
}
