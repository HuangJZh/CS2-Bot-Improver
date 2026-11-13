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
    (UtilityPurchasePolicy.EarliestPurchaseSeconds > 4.05f, "queued requests run before utility stage"),
    (UtilityPurchasePolicy.InventoryMatches("molotov","weapon_molotov"),"T fire occupies fire slot"),
    (UtilityPurchasePolicy.InventoryMatches("molotov","weapon_incgrenade"),"CT fire also occupies molotov category"),
    (UtilityPurchasePolicy.InventoryMatches("incgrenade","weapon_molotov"),"picked-up T fire prevents another CT fire purchase"),
    (UtilityPurchasePolicy.InventoryMatches("incgrenade","weapon_incgrenade"),"CT fire occupies fire category"),
    (!UtilityPurchasePolicy.InventoryMatches("molotov","weapon_smokegrenade"),"smoke cannot satisfy fire inventory"),
    (UtilityPurchasePolicy.InventoryMatches("flash","weapon_flashbang"),"other grenade categories preserved")
};
foreach(var (ok,name) in checks)
{
    if(!ok) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
Console.WriteLine($"{checks.Length} checks passed");
