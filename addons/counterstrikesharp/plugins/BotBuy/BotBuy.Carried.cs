using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;

namespace BotBuyPatch;
public sealed partial class BotBuyPatch
{
    private IBotControllerApi? _buyController;
    private readonly HashSet<int> _carriedBuyPlans = new();
    private readonly Dictionary<uint,HashSet<string>> _carriedPrimaries = new();
    private int _buyRoundGeneration;

    public override void Load(bool hotReload)
    {
        RegisterListener<Listeners.OnMapStart>(_ => ResetBuyState());
        RegisterListener<Listeners.OnMapEnd>(ResetBuyState);
    }
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        try { _buyController = new PluginCapability<IBotControllerApi>("botcontroller:api").Get(); }
        catch { _buyController = null; }
        if (_buyController == null) Logger.LogError("Carried primary purchase protection requires BotController API.");
    }
    public override void Unload(bool hotReload) => ResetBuyState();
    private void ResetBuyState()
    {
        _buyRoundGeneration++;
        _sniperBuyers.Clear();_sniperPolicyActive=false;
        ReleaseCarriedBuyPlans(); _carriedPrimaries.Clear();
        _prevWeapons.Clear(); _prevArmor.Clear(); _prevMoney.Clear();
        _botUserIdToIndex.Clear(); _botIndexCounter=0;
    }
    private void RoundTimer(float seconds, Action action)
    {
        int generation = _buyRoundGeneration;
        AddTimer(seconds, () => { if (generation == _buyRoundGeneration) action(); }, TimerFlags.STOP_ON_MAPCHANGE);
    }
    private void ReleaseCarriedBuyPlans()
    {
        foreach (int slot in _carriedBuyPlans) _buyController?.ClearBuyPlan(slot);
        _carriedBuyPlans.Clear();
    }
    private void ProtectCarriedPrimaries(IEnumerable<CCSPlayerController> players)
    {
        foreach (var player in players.Where(p => p is { IsValid:true, IsBot:true, PawnIsAlive:true }
            && !p.ControllingBot && !p.HasBeenControlledByPlayerThisRound))
        {
            var guns=WeaponNames(player).Where(PurchasePolicy.IsPrimary).ToHashSet();
            if(guns.Count==0) continue;
            _carriedPrimaries[player.EntityHandle.Raw]=guns;
            // A native buy plan completes the engine's buying state without issuing any primary buy.
            // Keep normal armor and kit purchases; existing guns are never dropped just to rebuy.
            var plan=new List<string>();
            // Preserve the existing policy: don't pay to repair armor above 40.
            AddNeededEquipment(player,plan);
            string aliases=string.Join(' ',plan);
            if (_buyController?.SetBuyPlan(player.Slot,aliases)==true)
            {
                _carriedBuyPlans.Add(player.Slot);
                Logger.LogInformation("Kept carried primary slot={Slot} weapons={Weapons}",player.Slot,string.Join(',',guns));
            }
            else Logger.LogError("Could not protect carried primary for slot {Slot}",player.Slot);
        }
    }
}
