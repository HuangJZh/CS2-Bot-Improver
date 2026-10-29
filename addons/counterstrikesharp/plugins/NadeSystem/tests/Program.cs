using System;
using NadeSystem;

var passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    Console.WriteLine("PASS " + name); passed++;
}
var low = UtilityPolicy.ForMode("less", 5);
Check(low.PerBot == 1 && low.PerTeam == 3, "low cannot let five bots throw four grenades each");
Check(UtilityPolicy.Allows(low,"he",0,0,0,0,10,0), "first legitimate HE can reserve a slot");
Check(!UtilityPolicy.Allows(low,"he",0,1,0,1,10.1f,14.5f), "another bot cannot burst immediately after teammate");
Check(!UtilityPolicy.Allows(low,"flash",0,1,1,0,20,0), "one low-mode flash per team per round");
Check(!UtilityPolicy.Allows(low,"smoke",1,1,0,0,20,0), "objective or retaliation cannot bypass bot total");
Check(!UtilityPolicy.Allows(low,"he",0,3,0,1,20,0), "team total counts all grenade types");
Check(!UtilityPolicy.Allows(low,"molotov",0,2,0,2,20,0), "HE and fire share explosive budget");
Check(!UtilityPolicy.Allows(UtilityPolicy.ForMode("off",5),"he",0,0,0,0,20,0), "off permits no grenades");
Check(UtilityPolicy.ForMode("less",1).PerTeam == 1, "one-bot teams still have one grenade");
Check(UtilityPolicy.KnownPositionWithin(10,20,30,11,10,0,0,0,100), "recent remembered position can justify a throw");
Check(!UtilityPolicy.KnownPositionWithin(10,20,30,9,10,0,0,0,100), "expired knowledge cannot justify a throw");
Check(!UtilityPolicy.KnownPositionWithin(500,500,0,11,10,0,0,0,100), "far remembered position is not a hidden near-target signal");
Check(UtilityPolicy.ApproximateSoundCoordinate(45) != 45, "audible sound coordinates are deliberately approximate");
Check(UtilityPolicy.ForMode("normal",5).TeamInterval < low.TeamInterval, "presets retain increasing activity");
Console.WriteLine($"{passed} utility policy checks passed.");
