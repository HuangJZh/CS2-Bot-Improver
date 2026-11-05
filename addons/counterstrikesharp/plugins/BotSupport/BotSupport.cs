using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace BotSupport;

public sealed class BotSupport : BasePlugin
{
    public override string ModuleName => "BotSupport";
    public override string ModuleVersion => "1.0.8-human-restored";
    public override string ModuleAuthor => "HuangJZh fork";
    public override string ModuleDescription => "Wheel weapon requests and probabilistic post-shot stops";
    private readonly Dictionary<int, float> _requests = new();
    private readonly Dictionary<int, (uint Pawn, string Request, float At)> _pending = new();
    private float _roundStartedAt;
    private int _buyRound;
    private bool _roundEnded;
    private string _difficulty = "Medium";
    private bool _nativeCombat=false;
    private static readonly HashSet<string> Firearms = new(StringComparer.OrdinalIgnoreCase)
    {
        "ak47", "aug", "famas", "galilar", "m4a1", "m4a1_silencer", "sg556",
        "awp", "ssg08", "scar20", "g3sg1", "mac10", "mp9", "mp7", "mp5sd", "ump45", "p90", "bizon",
        "glock", "hkp2000", "usp_silencer", "p250", "tec9", "fiveseven", "cz75a", "elite", "deagle", "revolver",
        "nova", "xm1014", "mag7", "sawedoff", "m249", "negev"
    };
    public override void Load(bool hotReload)
    {
        ReadDifficulty();
        AddCommandListener("playerchatwheel", Wheel, HookMode.Pre);
        AddCommandListener("needrop", (p,c) => Request(p,"auto"), HookMode.Pre);
        AddCommand("css_bot_request", "Request auto/awp/ak/m4a1/m4a4 from one bot", (p,c) => Request(p,c.ArgCount > 1 ? c.GetArg(1) : "auto"));
        RegisterEventHandler<EventWeaponFire>(OnFire, HookMode.Post);
        RegisterEventHandler<EventRoundStart>((e,i) => { ResetPurchases(); ReadDifficulty(); return HookResult.Continue; });
        RegisterEventHandler<EventRoundEnd>((e,i) => { _roundEnded=true; _pending.Clear(); _buyRound++; return HookResult.Continue; });
        RegisterListener<Listeners.OnMapStart>(map => { ResetPurchases(); ReadDifficulty(); });
        RegisterListener<Listeners.OnClientDisconnect>(slot => { _requests.Remove(slot); _pending.Remove(slot); });
    }
    private void ResetPurchases()
    {
        _requests.Clear(); _pending.Clear(); _buyRound++;
        _roundEnded=false;
        _roundStartedAt=Server.CurrentTime;
    }
    private void ReadDifficulty()
    {
        _nativeCombat=BotCombatPolicy.CombatBackend.Native(ModuleDirectory);
        // Match the actual installed preset, including user changes to reaction times.
        var overrides = Path.GetFullPath(Path.Combine(ModuleDirectory,"../../../../overrides"));
        try
        {
            var active = SHA256.HashData(File.ReadAllBytes(Path.Combine(overrides,"botprofile.vpk")));
            foreach (var level in new[] { "Low", "Medium", "High" })
                if (active.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(overrides,level,"botprofile.vpk")))))
                { _difficulty=level; Logger.LogInformation("Post-shot stop: {Difficulty} {Chance}%",level,SupportPolicy.StopChance(level)); return; }
            Logger.LogWarning("Unrecognized difficulty pack; keeping {Difficulty}",_difficulty);
        }
        catch (IOException error) { Logger.LogWarning(error,"Cannot read difficulty packs; keeping {Difficulty}",_difficulty); }
    }
    private HookResult OnFire(EventWeaponFire e, GameEventInfo info)
    {
        if(_nativeCombat)return HookResult.Continue;
        var p=e.Userid;
        if (p is not { IsValid:true, IsBot:true, PawnIsAlive:true } || p.ControllingBot || p.HasBeenControlledByPlayerThisRound
            || p.PlayerPawn.Value is not { IsValid:true } pawn || pawn.Bot == null
            || (pawn.Flags & 1) == 0 || !Firearms.Contains(e.Weapon.Replace("weapon_",""))
            || !SupportPolicy.StopSucceeds(_difficulty,Random.Shared.Next(100))) return HookResult.Continue;
        // Change horizontal velocity directly; teleporting can disturb ground contact.
        pawn.AbsVelocity.X = 0;
        pawn.AbsVelocity.Y = 0;
        return HookResult.Continue;
    }
    private HookResult Wheel(CCSPlayerController? p, CommandInfo c)
    {
        // Diagnostic: the game sends chat-wheel choices as the playerchatwheel
        // command with the localized token (e.g. #Chatwheel_requestweapon).
        var args=string.Join(' ',Enumerable.Range(1,Math.Max(0,c.ArgCount-1)).Select(c.GetArg));
        for(var i=1;i<c.ArgCount;i++)
            if(SupportPolicy.IsWeaponRequest(c.GetArg(i)))
            {
                Logger.LogInformation("Chat wheel weapon request: slot={Slot} args=[{Args}]",p?.Slot,args);
                return Request(p,"auto");
            }
        Logger.LogInformation("Chat wheel used (not a weapon request): slot={Slot} args=[{Args}]",p?.Slot,args);
        return HookResult.Continue;
    }
    private HookResult Request(CCSPlayerController? caller, string request)
    {
        if(caller is not { IsValid:true, IsBot:false, PawnIsAlive:true } || caller.TeamNum is not (2 or 3)
            || caller.PlayerPawn.Value is not { IsValid:true } pawn)
        {
            Logger.LogInformation("Weapon request ignored: caller is not a living human player");
            return HookResult.Continue;
        }
        if(HasPrimary(caller))
        {
            Logger.LogInformation("Weapon request rejected: slot={Slot} already has a primary weapon",caller.Slot);
            Reply(caller,"你已有主武器，请先丢下再请求。");
            return HookResult.Handled;
        }
        var rules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if(rules == null || _roundEnded || rules.BuyTimeEnded || !pawn.InBuyZone)
        {
            Logger.LogInformation("Weapon request rejected: slot={Slot} rules={HasRules} buyEnded={BuyEnded} inBuyZone={InZone}",
                caller.Slot,rules!=null,rules?.BuyTimeEnded,pawn.InBuyZone);
            Reply(caller,"请在购买时间内、购买区中请求发枪。");
            return HookResult.Handled;
        }
        if(_requests.TryGetValue(caller.Slot,out var last) && Server.CurrentTime-last < 2)
        {
            Logger.LogInformation("Weapon request rate-limited: slot={Slot}",caller.Slot);
            return HookResult.Handled;
        }
        // Let BotBuy finish equipping donors first. Recheck the caller, round,
        // inventory, buy zone, and balance when the queued request is executed.
        if(Server.CurrentTime-_roundStartedAt < 4f)
        {
            int slot=caller.Slot, round=_buyRound;
            if(_pending.ContainsKey(slot)) return HookResult.Handled;
            var queued=(caller.PlayerPawn.Raw,request,Server.CurrentTime);
            _pending[slot]=queued;
            AddTimer(Math.Max(0.05f,4.05f-(Server.CurrentTime-_roundStartedAt)),() =>
            {
                if(round!=_buyRound || !_pending.TryGetValue(slot,out var current) || current!=queued) return;
                _pending.Remove(slot);
                var player=Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller")
                    .FirstOrDefault(p => p.IsValid && p.Slot==slot && p.PlayerPawn.Raw==queued.Raw);
                if(player!=null) Request(player,request);
            });
            Reply(caller,"已收到要枪请求，稍后为你处理。");
            return HookResult.Handled;
        }
        var controllers=Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller").ToArray();
        var bots=controllers.Where(b => b.IsValid && b.IsBot && b.PawnIsAlive && !b.ControllingBot && !b.HasBeenControlledByPlayerThisRound
            && b.TeamNum==caller.TeamNum && b.PlayerPawn.Value is { IsValid:true, InBuyZone:true } bp
            && bp.Bot != null && !bp.Bot.IsAttacking && !bp.Bot.IsEnemyVisible
            && b.InGameMoneyServices != null).ToArray();
        var teamBots=controllers.Count(b => b is { IsValid:true, IsBot:true } && b.TeamNum==caller.TeamNum);
        Logger.LogInformation("Weapon request slot={Slot}: teammate bots={TeamBots} eligible donors={Donors}",
            caller.Slot,teamBots,bots.Length);
        var choice=SupportPolicy.Select(bots.Select(b => new Donor(b.Slot,b.InGameMoneyServices!.Account,Distance(b,caller))),caller.TeamNum,request);
        if(choice == null)
        {
            Logger.LogInformation("Weapon request failed: no donor can afford the requested weapon");
            Reply(caller,"没有符合条件的队友：需要在附近购买区、未交战，余额足够支付武器价格。可选 auto/awp/ak/m4a1/m4a4。");
            return HookResult.Handled;
        }
        _requests[caller.Slot]=Server.CurrentTime;
        var (donor,gun)=choice.Value;
        var bot=bots.Single(b => b.Slot==donor.Slot);
        var money=bot.InGameMoneyServices!;
        var before=money.Account;
        money.Account=before-gun.Price;
        try
        {
            if(caller.GiveNamedItem(gun.Item)==nint.Zero) throw new InvalidOperationException("Weapon creation failed");
        }
        catch(Exception error)
        {
            money.Account=before;
            Utilities.SetStateChanged(bot,"CCSPlayerController","m_pInGameMoneyServices");
            Logger.LogWarning(error,"Weapon purchase failed, refunded");
            Reply(caller,"购买失败，队友资金已退回。"); return HookResult.Handled;
        }
        Utilities.SetStateChanged(bot,"CCSPlayerController","m_pInGameMoneyServices");
        Logger.LogInformation("Weapon request fulfilled: slot={Slot} donor={Donor} gun={Gun} price={Price}",
            caller.Slot,bot.PlayerName,gun.Item,gun.Price);
        Reply(caller,$"{bot.PlayerName} 已花费 {gun.Price} 元给你购买 {gun.Label}。");
        return HookResult.Handled;
    }
    private static bool HasPrimary(CCSPlayerController p) => p.PlayerPawn.Value?.WeaponServices?.MyWeapons.Any(w => w.Value is { IsValid:true } weapon && weapon.As<CCSWeaponBase>().VData?.GearSlot==gear_slot_t.GEAR_SLOT_RIFLE)==true;
    private static double Distance(CCSPlayerController a,CCSPlayerController b)
    {
        var x=a.PlayerPawn.Value!.AbsOrigin!; var y=b.PlayerPawn.Value!.AbsOrigin!;
        return Math.Sqrt(Math.Pow(x.X-y.X,2)+Math.Pow(x.Y-y.Y,2)+Math.Pow(x.Z-y.Z,2));
    }
    private static void Reply(CCSPlayerController p,string text) => p.PrintToChat($"[BotSupport] {text}");
}
