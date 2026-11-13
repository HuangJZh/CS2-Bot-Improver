namespace NadeSystem;

public static class UtilityPurchasePolicy
{
    public static bool InventoryMatches(string type,string item)=>type switch
    {
        "molotov" or "incgrenade"=>item is "weapon_molotov" or "weapon_incgrenade",
        "smoke"=>item=="weapon_smokegrenade",
        "flash"=>item=="weapon_flashbang",
        "he"=>item=="weapon_hegrenade",
        _=>false
    };
    public const float EarliestPurchaseSeconds = 5f;

    public static bool CanSpend(bool hasPrimary, bool pistolRound, int money, bool teammateNeedsGun)
        => !teammateNeedsGun && (hasPrimary || (pistolRound && money >= 0 && money < 1050));
}
