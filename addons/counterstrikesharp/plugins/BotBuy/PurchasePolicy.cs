namespace BotBuyPatch;

public static class PurchasePolicy
{
    public static bool IsForbidden(string weapon) => weapon is "weapon_aug" or "weapon_sg556";
    public static bool IsPrimary(string weapon) => weapon is
        "weapon_ak47" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug" or "weapon_sg556"
        or "weapon_famas" or "weapon_galilar" or "weapon_awp" or "weapon_ssg08" or "weapon_scar20" or "weapon_g3sg1"
        or "weapon_mac10" or "weapon_mp9" or "weapon_mp7" or "weapon_mp5sd" or "weapon_ump45" or "weapon_p90" or "weapon_bizon"
        or "weapon_nova" or "weapon_xm1014" or "weapon_mag7" or "weapon_sawedoff" or "weapon_negev" or "weapon_m249";
    public static bool HasPrimary(IEnumerable<string> inventory) => inventory.Any(IsPrimary);
    public static bool CanBuy(string item, IEnumerable<string> inventory) => !IsForbidden(item)
        && (!IsPrimary(item) || !HasPrimary(inventory));
    public static bool CanSwap(string oldItem, string newItem, IEnumerable<string> carried)
        => oldItem != newItem && !IsForbidden(newItem) && !(IsPrimary(oldItem) && carried.Contains(oldItem));
}
