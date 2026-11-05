namespace BotState;
public partial class BotState
{
    private bool _nativeCombat=false;
    private void ReadCombatBackend()=>_nativeCombat=BotCombatPolicy.CombatBackend.Native(ModuleDirectory);
}
