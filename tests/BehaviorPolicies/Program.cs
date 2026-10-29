using System.Numerics;
using BotTeamplay;
using BotState;
using BotAimImprover;
using BotAI;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL " + name);
    count++;
    Console.WriteLine("PASS " + name);
}
Check(TeamplayPolicy.Parse("#Chatwheel_requestweapon") == TeamOrder.Weapon, "installed wheel requestweapon maps to one-donor request");
Check(TeamplayPolicy.Parse("#Chatwheel_aplan") == TeamOrder.ScoutA, "installed wheel A plan maps to scout A");
Check(TeamplayPolicy.Parse("#Chatwheel_bplan") == TeamOrder.ScoutB, "installed wheel B plan maps to scout B");
Check(TeamplayPolicy.Parse("#Chatwheel_followme") == TeamOrder.Follow, "installed wheel follow maps to follow");
Check(TeamplayPolicy.Parse("#Chatwheel_followingyou") == TeamOrder.None, "I follow you does not invert the intended command");
var candidates = new[] {
    new BotCandidate(1, 16000, 20, true, true, true, true),
    new BotCandidate(2, 3699, 40, true, false, true, true),
    new BotCandidate(3, 8000, 60, true, false, false, true),
    new BotCandidate(4, 8000, 80, true, false, true, false),
    new BotCandidate(5, 6000, 100, true, false, true, true),
    new BotCandidate(6, 16000, 300, true, false, true, true),
};
Check(TeamplayPolicy.Donor(candidates, 2900, 800, 1500)?.Slot == 5, "exactly the nearest eligible armed donor is chosen");
Check(TeamplayPolicy.Donor(candidates.Take(4), 2900, 800, 1500) == null, "busy, poor, unarmed and outside-buy-zone donors are excluded");
Check(TeamplayPolicy.Donor([new(9,3700,10,true,false,true,true)],2900,800,1500)?.Slot == 9, "reserve boundary allows exactly enough funds");
Check(TeamplayPolicy.Donor([new(9,16000,double.NaN,true,false,true,true)],2900,800,1500) == null, "invalid positions cannot become donors");
Check(TeamplayPolicy.Responders(candidates, 1, 1500).Length == 1, "a site command selects one scout");
Check(TeamplayPolicy.Responders(candidates, 99, 1500).Length == 2, "even misconfigured follow cannot send every bot");
Check(TeamplayPolicy.CoolingDown(11,10,4) && !TeamplayPolicy.CoolingDown(14,10,4), "request cooldown boundary");
Check(HumanReactionPolicy.OutsideScreen(0,180), "rear hit requires orientation delay");
Check(!HumanReactionPolicy.OutsideScreen(0,30), "front hit gets no extra stacked delay");
Check(Math.Abs(HumanReactionPolicy.YawDifference(179,-179)-2) < .001, "FOV wraps across plus/minus 180");
Check(HumanReactionPolicy.RearDelay("low").Min >= HumanReactionPolicy.RearDelay("medium").Max,
    "low rear response remains slower than medium");
Check(HumanReactionPolicy.RearDelay("medium").Min >= HumanReactionPolicy.RearDelay("high").Max,
    "high rear response remains faster than medium");
Check(AimVisibilityPolicy.SegmentIntersectsSmoke(new(-200,0,0),new(200,0,0),Vector3.Zero,155), "smoke between bot and enemy blocks live aim");
Check(!AimVisibilityPolicy.SegmentIntersectsSmoke(new(200,0,0),new(400,0,0),Vector3.Zero,155), "smoke behind bot does not block visible target");
Check(AimVisibilityPolicy.SegmentIntersectsSmoke(Vector3.Zero,new(400,0,0),Vector3.Zero,155), "bot inside smoke is uncertain");
Check(AimVisibilityPolicy.SmokeRadius(20) == 0 && AimVisibilityPolicy.SmokeRadius(.35f) < AimVisibilityPolicy.SmokeRadius(1), "smoke growth and expiry");
foreach (var name in new[] { "AttackState_SkipSniperSpreadCheck", "IsNoticable_AlwaysTrue", "OnAudibleEvent_GlobalHearRange", "Upkeep_BotSIN_ZeroDrift" })
{
    Check(!CombatPatchPolicy.IsAllowed(name), "unfair patch disabled: " + name);
    Check(!CombatPatchPolicy.IsAllowed(name + "_Cave"), "matching code cave disabled: " + name);
}
Check(CombatPatchPolicy.IsAllowed("UnrelatedNavigationFix"), "navigation fixes are retained");
Console.WriteLine($"{count} behavior policy checks passed.");

Check(BrakePolicy.Chance("low") < BrakePolicy.Chance("medium") && BrakePolicy.Chance("medium") < BrakePolicy.Chance("high"), "brake assistance chance increases by difficulty");
Check(BrakePolicy.NeedsStop("weapon_ak47",700), "distant rifle prepares a stop");
Check(BrakePolicy.NeedsStop("weapon_awp",100), "sniper prepares a stop");
Check(BrakePolicy.NeedsStop("weapon_deagle",500), "precision pistol prepares a stop");
Check(!BrakePolicy.NeedsStop("weapon_tec9",80), "close Tec9 retains running fire");
Check(!BrakePolicy.NeedsStop("weapon_mp9",80), "close SMG retains running fire");
Check(BrakePolicy.NeedsStop("weapon_mp9",1000), "distant SMG may prefer a stop");
Console.WriteLine($"Final total: {count} behavior checks passed.");

Check(BrakePolicy.StopPreference("weapon_ak47",80)==0,"point-blank rifle retains running fire");
Check(BrakePolicy.StopPreference("weapon_awp",40)==1,"point-blank sniper retains stop intent");
Check(BrakePolicy.StopPreference("weapon_ak47",90)<BrakePolicy.StopPreference("weapon_ak47",110),"rifle stop preference grows with range");
Check(BrakePolicy.StopPreference("weapon_mp9",80)<BrakePolicy.StopPreference("weapon_deagle",80),"contact SMG may run while Deagle wants to stop");
Check(BrakePolicy.StopPreference("weapon_ak47",2000)==1,"distant rifle always wants to stop, execution still depends on skill");
Check(BrakePolicy.StopPreference("weapon_ak47",float.NaN)==0,"invalid range cannot trigger assistance");
Console.WriteLine($"Distance-aware total: {count} checks passed.");

Check(BrakePolicy.StopPreference("weapon_ak47",120)==1,"rifle wants to stop beyond three-metre band");
Check(BrakePolicy.StopPreference("weapon_glock",100)==.5,"ordinary pistol blends intent within two-to-three-metre band");
Check(BrakePolicy.StopPreference("weapon_nova",40)==1,"shotgun retains stop intent even at contact");
Check(BrakePolicy.StopPreference("weapon_negev",40)==1,"machinegun retains stop intent even at contact");
Check(BrakePolicy.StopPreference("weapon_knife",40)==0,"knife is never treated as a gun brake request");
Console.WriteLine($"Final distance policy total: {count} checks passed.");
