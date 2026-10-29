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
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "HuangJZh fork";
    public override string ModuleDescription => "Wheel weapon requests and probabilistic post-shot stops";
    private readonly Dictionary<int, float> _requests = new();
    private string _difficulty = "Medium";
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
        RegisterEventHandler<EventRoundStart>((e,i) => { _requests.Clear(); ReadDifficulty(); return HookResult.Continue; });
        RegisterListener<Listeners.OnMapStart>(map => { _requests.Clear(); ReadDifficulty(); });
        RegisterListener<Listeners.OnClientDisconnect>(slot => _requests.Remove(slot));
    }
    private void ReadDifficulty()
    {
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
        var p=e.Userid;
        if (p is not { IsValid:true, IsBot:true, PawnIsAlive:true } || p.ControllingBot || p.HasBeenControlledByPlayerThisRound
            || p.PlayerPawn.Value is not { IsValid:true } pawn || pawn.Bot == null
            || (pawn.Flags & 1) == 0 || !Firearms.Contains(e.Weapon.Replace("weapon_",""))
            || !SupportPolicy.StopSucceeds(_difficulty,Random.Shared.Next(100))) return HookResult.Continue;
        pawn.Teleport((Vector?)null, null, new Vector(0,0,pawn.AbsVelocity.Z));
        return HookResult.Continue;
    }
    private HookResult Wheel(CCSPlayerController? p, CommandInfo c)
    {
        for(var i=1;i<c.ArgCount;i++) if(SupportPolicy.IsWeaponRequest(c.GetArg(i))) return Request(p,"auto");
        return HookResult.Continue;
    }
    private HookResult Request(CCSPlayerController? caller, string request)
    {
        if(caller is not { IsValid:true, IsBot:false, PawnIsAlive:true } || caller.TeamNum is not (2 or 3)
            || caller.PlayerPawn.Value is not { IsValid:true } pawn) return HookResult.Continue;
        if(_requests.TryGetValue(caller.Slot,out var last) && Server.CurrentTime-last < 2) return HookResult.Handled;
        _requests[caller.Slot]=Server.CurrentTime;
        if(HasPrimary(caller)) { Reply(caller,"你已有主武器，请先丢下再请求。"); return HookResult.Handled; }
        var rules=Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if(rules == null || rules.BuyTimeEnded || !pawn.InBuyZone)
        { Reply(caller,"请在购买时间内、购买区中请求发枪。"); return HookResult.Handled; }
        var bots=Utilities.GetPlayers().Where(b => b.IsValid && b.IsBot && b.PawnIsAlive && !b.ControllingBot && !b.HasBeenControlledByPlayerThisRound
            && b.TeamNum==caller.TeamNum && b.PlayerPawn.Value is { IsValid:true, InBuyZone:true } bp
            && bp.Bot != null && !bp.Bot.IsAttacking && !bp.Bot.IsEnemyVisible && HasPrimary(b)
            && b.InGameMoneyServices != null).ToArray();
        var choice=SupportPolicy.Select(bots.Select(b => new Donor(b.Slot,b.InGameMoneyServices!.Account,Distance(b,caller))),caller.TeamNum,request);
        if(choice == null) { Reply(caller,"没有符合条件的队友：需要在附近购买区、有主武器，购买后留有 800 元。可选 auto/awp/ak/m4a1/m4a4。"); return HookResult.Handled; }
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
