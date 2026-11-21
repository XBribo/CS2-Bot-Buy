namespace BotBuyPatch;

internal static class GiftPolicy
{
    internal const int PoorLimit = 2800;
    internal static int RiflePrice(bool ct) => ct ? 2900 : 2700;
    internal static int RifleCount(bool ct, int money) => money < 2900 ? 0 : Math.Min(3, money / RiflePrice(ct));
    internal static string Rifle(bool ct, Random rng) =>
        !ct ? "weapon_ak47" : rng.Next(2) == 0 ? "weapon_m4a1_silencer" : "weapon_m4a1";

    internal static (string Item, int Price)? Armor(bool ct, int money) =>
        money >= 1000 ? ("item_assaultsuit", 1000) : ct && money >= 650 ? ("item_kevlar", 650) : null;
}
