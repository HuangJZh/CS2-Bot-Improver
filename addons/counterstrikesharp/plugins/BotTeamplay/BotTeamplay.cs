using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Vec = System.Numerics.Vector3;

namespace BotTeamplay;

public sealed class TeamplayConfig
{
    public bool Enabled { get; set; } = true;
    public int DonorReserve { get; set; } = 800;
    public float WeaponCooldownSeconds { get; set; } = 10;
    public float OrderCooldownSeconds { get; set; } = 4;
    public float LeaseSeconds { get; set; } = 20;
    public int FollowResponders { get; set; } = 1;
    public float MaxOrderDistance { get; set; } = 2500;
    public float MaxDonorDistance { get; set; } = 1500;
}

public sealed class BotTeamplay : BasePlugin
{
    public override string ModuleName => "BotTeamplay";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "HuangJZh fork";
    public override string ModuleDescription => "Bounded radio assistance and native bot order leases";
    private TeamplayConfig _config = new();
    private readonly Dictionary<ulong, float> _weaponRequests = new();
    private readonly Dictionary<ulong, float> _orderRequests = new();
    private readonly Dictionary<int, Lease> _leases = new();

    private sealed class Lease
    {
        public required CCSPlayerController Bot;
        public required CCSPlayerController Caller;
        public required uint PawnGeneration;
        public required uint CallerPawnGeneration;
        public required TeamOrder Kind;
        public required Vec Destination;
        public required float Expires;
        public required Vec LastProgressPosition;
        public required float LastProgressAt;
        public bool PreviousFollowing;
        public uint PreviousLeader;
        public float PreviousFollowTimestamp;
        public Vec PreviousNoisePosition;
        public float PreviousNoiseTimestamp;
        public float PublishedNoiseTimestamp = -1;
    }

    public override void Load(bool hotReload)
    {
        var path = Path.Combine(ModuleDirectory, "teamplay.json");
        if (File.Exists(path)) _config = JsonSerializer.Deserialize<TeamplayConfig>(File.ReadAllText(path)) ?? new();
        else File.WriteAllText(path, JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true }));
        _config.DonorReserve = Math.Clamp(_config.DonorReserve, 0, 16000);
        _config.FollowResponders = Math.Clamp(_config.FollowResponders, 1, 2);
        _config.LeaseSeconds = Finite(_config.LeaseSeconds, 20, 5, 45);
        _config.WeaponCooldownSeconds = Finite(_config.WeaponCooldownSeconds, 10, 3, 60);
        _config.OrderCooldownSeconds = Finite(_config.OrderCooldownSeconds, 4, 1, 30);
        _config.MaxOrderDistance = Finite(_config.MaxOrderDistance, 2500, 200, 6000);
        _config.MaxDonorDistance = Finite(_config.MaxDonorDistance, 1500, 100, 3000);
        AddCommandListener("playerchatwheel", Wheel);
        foreach (var alias in new[] { "needrop", "followme", "go_a", "go_b" })
            AddCommandListener(alias, (p, c) => Dispatch(p, TeamplayPolicy.Parse(c.GetArg(0))));
        AddCommand("css_bot_request", "Ask exactly one teammate to buy a rifle", (p,c) => Dispatch(p, TeamOrder.Weapon));
        AddCommand("css_bot_follow", "Ask nearby idle bots to follow", (p,c) => Dispatch(p, TeamOrder.Follow));
        AddCommand("css_bot_a", "Assign one idle scout to site A", (p,c) => Dispatch(p, TeamOrder.ScoutA));
        AddCommand("css_bot_b", "Assign one idle scout to site B", (p,c) => Dispatch(p, TeamOrder.ScoutB));
        AddCommand("css_bot_cancel", "Cancel your bot order leases", (p,c) => Dispatch(p, TeamOrder.Cancel));
        RegisterEventHandler<EventRoundStart>((e,i) => { Clear(); return HookResult.Continue; });
        RegisterListener<Listeners.OnMapEnd>(Clear);
        AddTimer(.25f, UpdateLeases, TimerFlags.REPEAT);
    }

    private static float Finite(float value, float fallback, float min, float max) => float.IsFinite(value) ? Math.Clamp(value,min,max) : fallback;
    private HookResult Wheel(CCSPlayerController? caller, CommandInfo command)
    {
        for (var i = 1; i < command.ArgCount; i++)
        {
            var order = TeamplayPolicy.Parse(command.GetArg(i));
            if (order != TeamOrder.None) return Dispatch(caller, order);
        }
        return HookResult.Continue;
    }

    private HookResult Dispatch(CCSPlayerController? caller, TeamOrder order)
    {
        if (!_config.Enabled || order == TeamOrder.None || caller is not { IsValid: true, IsBot: false, PawnIsAlive: true }
            || caller.TeamNum is not (2 or 3) || caller.PlayerPawn.Value is not { IsValid: true }) return HookResult.Continue;
        if (order == TeamOrder.Cancel)
        {
            foreach (var slot in _leases.Where(x => x.Value.Caller.Handle == caller.Handle).Select(x => x.Key).ToArray()) Release(slot);
            Reply(caller,"已取消你的机器人指令。");
            return HookResult.Handled;
        }
        if (order == TeamOrder.Weapon) BuyFor(caller);
        else Assign(caller, order);
        // We handle recognized orders once; do not also broadcast a native order to every bot.
        return HookResult.Handled;
    }

    private void BuyFor(CCSPlayerController caller)
    {
        if (_weaponRequests.TryGetValue(caller.SteamID, out var last) && TeamplayPolicy.CoolingDown(Server.CurrentTime,last,_config.WeaponCooldownSeconds))
        { Reply(caller,"发枪请求正在冷却。"); return; }
        if (HasPrimary(caller)) { Reply(caller,"你已经有主武器。"); return; }
        var rules = Rules();
        if (rules == null || rules.BuyTimeEnded || !caller.PlayerPawn.Value!.InBuyZone)
        { Reply(caller,"请在购买时间内、购买区域中请求发枪。"); return; }
        var item = caller.TeamNum == 2 ? "weapon_ak47" : "weapon_m4a1";
        var price = caller.TeamNum == 2 ? 2700 : 2900;
        var bots = Teammates(caller).ToArray();
        var donor = TeamplayPolicy.Donor(bots.Select(b => Candidate(b,caller)),price,_config.DonorReserve,_config.MaxDonorDistance);
        if (donor == null) { Reply(caller,"附近没有买好主武器且余钱足够的空闲队友。"); return; }
        var bot = bots.Single(b => b.Slot == donor.Value.Slot);
        var money = bot.InGameMoneyServices!;
        var before = money.Account;
        // The server executes this synchronously: reserve funds before spawning the item.
        money.Account -= price;
        try
        {
            if (caller.GiveNamedItem(item) == nint.Zero) throw new InvalidOperationException("GiveNamedItem returned no weapon");
        }
        catch (Exception error)
        {
            money.Account = before;
            Utilities.SetStateChanged(bot,"CCSPlayerController","m_pInGameMoneyServices");
            Logger.LogWarning(error,"Weapon request failed; donor funds restored");
            Reply(caller,"购买失败，队友资金已退还。");
            return;
        }
        Utilities.SetStateChanged(bot,"CCSPlayerController","m_pInGameMoneyServices");
        _weaponRequests[caller.SteamID] = Server.CurrentTime;
        foreach (var teammate in Utilities.GetPlayers().Where(p => p.IsValid && p.TeamNum == caller.TeamNum))
            Reply(teammate,$"{bot.PlayerName} 为 {caller.PlayerName} 购买了一把{(caller.TeamNum == 2 ? " AK-47" : " M4A4")}。");
    }

    private void Assign(CCSPlayerController caller, TeamOrder kind)
    {
        if (_orderRequests.TryGetValue(caller.SteamID,out var last) && TeamplayPolicy.CoolingDown(Server.CurrentTime,last,_config.OrderCooldownSeconds))
        { Reply(caller,"指令正在冷却。"); return; }
        var destination = Position(caller);
        if (kind is TeamOrder.ScoutA or TeamOrder.ScoutB)
        {
            var resource = Utilities.FindAllEntitiesByDesignerName<CCSPlayerResource>("cs_player_manager").FirstOrDefault();
            if (resource == null) { Reply(caller,"当前地图没有可读取的 A/B 点信息。"); return; }
            destination = Copy(kind == TeamOrder.ScoutA ? resource.BombsiteCenterA : resource.BombsiteCenterB);
            if (destination.LengthSquared() < 1) { Reply(caller,"当前地图未提供该包点的位置。"); return; }
        }
        var bots = Teammates(caller).Where(b => !_leases.ContainsKey(b.Slot)).ToArray();
        var slots = TeamplayPolicy.Responders(bots.Select(b => Candidate(b,caller)),kind == TeamOrder.Follow ? _config.FollowResponders : 1,_config.MaxOrderDistance);
        if (slots.Length == 0) { Reply(caller,"附近没有能接令的空闲队友；交战、安包和拆包优先。"); return; }
        foreach (var slot in slots)
        {
            var botPlayer = bots.Single(b => b.Slot == slot);
            var bot = botPlayer.PlayerPawn.Value!.Bot!;
            var lease = new Lease { Bot=botPlayer, Caller=caller, PawnGeneration=botPlayer.PlayerPawn.Raw,
                CallerPawnGeneration=caller.PlayerPawn.Raw,
                Kind=kind, Destination=destination, Expires=Server.CurrentTime+_config.LeaseSeconds,
                LastProgressPosition=Position(botPlayer), LastProgressAt=Server.CurrentTime,
                PreviousFollowing=bot.IsFollowing, PreviousLeader=bot.Leader.Raw, PreviousFollowTimestamp=bot.FollowTimestamp,
                PreviousNoisePosition=Copy(bot.NoisePosition), PreviousNoiseTimestamp=bot.NoiseTimestamp };
            _leases.Add(slot,lease);
            Publish(lease);
        }
        _orderRequests[caller.SteamID] = Server.CurrentTime;
        Reply(caller,$"指令已交给 {string.Join("、",bots.Where(b => slots.Contains(b.Slot)).Select(b => b.PlayerName))}；其余队友保持原任务。");
    }

    private void Publish(Lease lease)
    {
        var bot = lease.Bot.PlayerPawn.Value!.Bot!;
        if (lease.Kind == TeamOrder.Follow)
        {
            bot.Leader.Raw = lease.CallerPawnGeneration;
            bot.IsFollowing = true;
            bot.FollowTimestamp = Server.CurrentTime;
        }
        else
        {
            // A human site order is legitimate team information, not an enemy location.
            // The engine chooses a navmesh route through its existing investigation behavior.
            Set(bot.NoisePosition,lease.Destination);
            bot.NoiseTravelDistance = Vec.Distance(Position(lease.Bot),lease.Destination);
            bot.NoiseTimestamp = Server.CurrentTime;
            bot.BendNoisePositionValid = false;
            lease.PublishedNoiseTimestamp = bot.NoiseTimestamp;
        }
        bot.RepathTimer.Duration = 0;
        bot.RepathTimer.Timestamp = Server.CurrentTime;
        bot.RepathTimer.Timescale = 1;
    }

    private void UpdateLeases()
    {
        foreach (var (slot,lease) in _leases.ToArray())
        {
            if (!Alive(lease.Bot) || !Alive(lease.Caller) || lease.Bot.TeamNum != lease.Caller.TeamNum
                || lease.Bot.PlayerPawn.Raw != lease.PawnGeneration || lease.Caller.PlayerPawn.Raw != lease.CallerPawnGeneration
                || Server.CurrentTime >= lease.Expires || Busy(lease.Bot))
            { Release(slot); continue; }
            var destination = lease.Kind == TeamOrder.Follow ? Position(lease.Caller) : lease.Destination;
            var position = Position(lease.Bot);
            if (Vec.Distance(position,destination) < 160)
            {
                lease.LastProgressAt = Server.CurrentTime;
                if (lease.Kind != TeamOrder.Follow) { Reply(lease.Caller,$"{lease.Bot.PlayerName} 已到达侦察区域。"); Release(slot); continue; }
            }
            if (Vec.Distance(position,lease.LastProgressPosition) > 40)
            { lease.LastProgressPosition=position; lease.LastProgressAt=Server.CurrentTime; }
            if (Server.CurrentTime-lease.LastProgressAt > 6 && Rules()?.FreezePeriod != true)
            { Reply(lease.Caller,$"{lease.Bot.PlayerName} 未能继续执行指令，已恢复自主行动。"); Release(slot); continue; }
            Publish(lease);
        }
    }

    private void Release(int slot)
    {
        if (!_leases.Remove(slot,out var lease) || !Alive(lease.Bot) || lease.Bot.PlayerPawn.Raw != lease.PawnGeneration) return;
        var bot=lease.Bot.PlayerPawn.Value!.Bot!;
        if (lease.Kind == TeamOrder.Follow && bot.Leader.Raw == lease.CallerPawnGeneration)
        { bot.IsFollowing=lease.PreviousFollowing; bot.Leader.Raw=lease.PreviousLeader; bot.FollowTimestamp=lease.PreviousFollowTimestamp; }
        if (lease.Kind != TeamOrder.Follow && bot.NoiseTimestamp == lease.PublishedNoiseTimestamp)
        { Set(bot.NoisePosition,lease.PreviousNoisePosition); bot.NoiseTimestamp=lease.PreviousNoiseTimestamp; }
    }

    private void Clear() { foreach (var slot in _leases.Keys.ToArray()) Release(slot); _weaponRequests.Clear(); _orderRequests.Clear(); }
    public override void Unload(bool hotReload) => Clear();
    private static CCSGameRules? Rules() => Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
    private static bool Alive(CCSPlayerController p) => p.IsValid && p.PawnIsAlive && p.PlayerPawn.Value is { IsValid:true };
    private static IEnumerable<CCSPlayerController> Teammates(CCSPlayerController caller) => Utilities.GetPlayers().Where(p => p.IsBot && Alive(p) && p.TeamNum==caller.TeamNum && p.PlayerPawn.Value!.Bot != null);
    private static bool Busy(CCSPlayerController p)
    {
        var pawn=p.PlayerPawn.Value; var bot=pawn?.Bot;
        return pawn == null || pawn.IsDefusing || bot == null || bot.IsAttacking || bot.IsEnemyVisible
            || pawn.WeaponServices?.MyWeapons.Any(w => w.Value?.DesignerName == "weapon_c4") == true;
    }
    private BotCandidate Candidate(CCSPlayerController bot,CCSPlayerController caller) => new(bot.Slot,bot.InGameMoneyServices?.Account ?? 0,Vec.Distance(Position(bot),Position(caller)),Alive(bot),Busy(bot),bot.PlayerPawn.Value!.InBuyZone,HasPrimary(bot));
    private static bool HasPrimary(CCSPlayerController p) => p.PlayerPawn.Value?.WeaponServices?.MyWeapons.Any(w => w.Value is { IsValid:true } weapon && weapon.As<CCSWeaponBase>().VData?.GearSlot == gear_slot_t.GEAR_SLOT_RIFLE) == true;
    private static Vec Position(CCSPlayerController p) => Copy(p.PlayerPawn.Value!.AbsOrigin!);
    private static Vec Copy(Vector v) => new(v.X,v.Y,v.Z);
    private static void Set(Vector v, Vec p) { v.X=p.X; v.Y=p.Y; v.Z=p.Z; }
    private static void Reply(CCSPlayerController p,string text) { if(p.IsValid) p.PrintToChat($"[BotTeamplay] {text}"); }
}
