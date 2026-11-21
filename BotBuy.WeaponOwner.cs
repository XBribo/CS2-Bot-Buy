using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace BotBuyPatch;

internal static class WeaponOwner
{
    internal static void OnPurchase(CCSPlayerController bot, string item)
    {
        if (item.StartsWith("item_") || bot.PlayerPawn.Value?.WeaponServices is not { } inventory) return;
        string alias = item.StartsWith("weapon_") ? item[7..] : item;
        var gun = RoundBuyPolicy.FindGun(alias);
        string name = "weapon_" + alias;
        CCSWeaponBase? bought = null;
        foreach (var handle in inventory.MyWeapons)
        {
            if (handle.Value is not { IsValid: true } weapon) continue;
            if (gun != null ? weapon.AttributeManager.Item.ItemDefinitionIndex != gun.Id : weapon.DesignerName != name) continue;
            if (bought != null) return;
            bought = weapon.As<CCSWeaponBase>();
        }
        if (bought != null) SetIfUnowned(bought, bot.SteamID);
    }

    internal static void SetIfUnowned(CCSWeaponBase weapon, ulong xuid)
    {
        if (xuid == 0 || CoreConfig.FollowCS2ServerGuidelines || weapon.PrevOwner.IsValid ||
            weapon.OriginalOwnerXuidLow != 0 || weapon.OriginalOwnerXuidHigh != 0) return;
        weapon.OriginalOwnerXuidLow = (uint)xuid;
        weapon.OriginalOwnerXuidHigh = (uint)(xuid >> 32);
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidLow");
        Utilities.SetStateChanged(weapon, "CEconEntity", "m_OriginalOwnerXuidHigh");
    }
}
