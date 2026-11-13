using BotCombatPolicy;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Cvars;
namespace BotState;

public partial class BotState
{
    private ConVar? _retakeTreeVar;
    private int _retakeCheckTick=int.MinValue;
    private bool _retakeTreeActive;
    private bool RetakeOwnsGoals
    {
        get
        {
            if(_retakeCheckTick!=Server.TickCount)
            {
                _retakeCheckTick=Server.TickCount;
                _retakeTreeVar??=ConVar.Find("mp_bot_ai_bt");
                _retakeTreeActive=_retakeTreeVar?.StringValue==RetakeAiContract.Tree;
            }
            return _retakeTreeActive;
        }
    }
}
