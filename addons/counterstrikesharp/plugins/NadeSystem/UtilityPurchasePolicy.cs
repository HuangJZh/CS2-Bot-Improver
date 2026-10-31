namespace NadeSystem;

public static class UtilityPurchasePolicy
{
    public const float EarliestPurchaseSeconds = 5f;

    public static bool CanSpend(bool hasPrimary, bool pistolRound, int money, bool teammateNeedsGun)
        => !teammateNeedsGun && (hasPrimary || (pistolRound && money >= 0 && money < 1050));
}
