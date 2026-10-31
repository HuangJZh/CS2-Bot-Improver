using NadeSystem;
var checks = new (bool Result, string Name)[]
{
    (!UtilityPurchasePolicy.CanSpend(false,false,5000,false), "save funds for own primary"),
    (!UtilityPurchasePolicy.CanSpend(false,false,800,false), "save during non-pistol eco"),
    (UtilityPurchasePolicy.CanSpend(false,true,800,false), "pistol round may buy utility after pistol and armor"),
    (!UtilityPurchasePolicy.CanSpend(false,true,5000,false), "rich pistol-round bot still needs own primary"),
    (UtilityPurchasePolicy.CanSpend(true,false,5000,false), "armed bot may spend remaining funds"),
    (!UtilityPurchasePolicy.CanSpend(true,false,16000,true), "weaponless teammate takes priority even with abundant funds"),
    (!UtilityPurchasePolicy.CanSpend(false,true,800,true), "teammate request precedes pistol utility"),
    (UtilityPurchasePolicy.EarliestPurchaseSeconds > 4.05f, "queued requests run before utility stage")
};
foreach(var (ok,name) in checks)
{
    if(!ok) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
Console.WriteLine($"{checks.Length} checks passed");
