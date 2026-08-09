using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using BotControllerApi;

namespace BotBuyPatch;

public sealed partial class BotBuyPatch : BasePlugin
{
    // Schedules the legacy direct-buy workflow when BotController takeover is unavailable
    private void ScheduleLegacyPurchases(
        List<CCSPlayerController> allPlayers,
        List<CCSPlayerController> allCT,
        List<CCSPlayerController> ctBots,
        List<CCSPlayerController> tBots)
    {
        // Legacy direct-buy path remains the fallback when the demo is disabled
        // Swap HKP2000
        foreach (var player in allPlayers.Where(p => p.IsValid && p.IsBot))
        {
            // Swap HKP2000
            if (Random.Shared.NextSingle() < 0.8f)
            {
                Swap(player, "weapon_hkp2000", "weapon_usp_silencer");
            }
        }
        // Force Buy
        bool allCtInRange = ctBots.Count > 0 && ctBots.All(p =>
            p.InGameMoneyServices != null && p.InGameMoneyServices.Account > 1000 && p.InGameMoneyServices.Account < 2800);

        bool allTInRange = tBots.Count > 0 && tBots.All(p =>
            p.InGameMoneyServices != null && p.InGameMoneyServices.Account > 1000 && p.InGameMoneyServices.Account < 2800);
        AddTimer(0.4f, () =>
        {
            if (allCtInRange)
            {
                float roll = Random.Shared.NextSingle();
                foreach (var bot in ctBots)
                {
                    if (roll < 0.10f)
                    {
                        Swap(bot, "weapon_usp_silencer", "weapon_fiveseven");
                        Swap(bot, "weapon_hkp2000", "weapon_fiveseven");
                    }
                    else if (roll < 0.20f)
                    {
                        Buy(bot, "weapon_mp9");
                    }
                }
            }

            if (allTInRange)
            {
                float roll = Random.Shared.NextSingle();
                foreach (var bot in tBots)
                {
                    if (roll < 0.10f)
                    {
                        Swap(bot, "weapon_glock", "weapon_tec9");
                    }
                    else if (roll < 0.20f)
                    {
                        Buy(bot, "weapon_mac10");
                    }
                }
            }
        });
        // Don't buy if we have scar20/g3sg1
        foreach (var player in allPlayers.Where(p => p.IsValid && p.IsBot))
        {
            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

            var activeWeapon = pawn.WeaponServices.ActiveWeapon.Value;
            if (activeWeapon == null) continue;

            string initialGun = activeWeapon.DesignerName;
            if (initialGun != "weapon_scar20" && initialGun != "weapon_g3sg1") continue;

            var copyPlayer = player;
            AddTimer(0.5f, () =>
            {
                if (!copyPlayer.IsValid) return;
                var p2 = copyPlayer.PlayerPawn.Value;
                if (p2 == null || !p2.IsValid || p2.WeaponServices == null) return;

                var currentWeapon = p2.WeaponServices.ActiveWeapon.Value;
                if (currentWeapon == null) return;

                string currentGun = currentWeapon.DesignerName;
                if (currentGun != "weapon_scar20" && currentGun != "weapon_g3sg1")
                {
                    Refund(copyPlayer, currentGun);
                }
            });
        }
        // Swap AUG
        foreach (var player in allPlayers.Where(p => p.IsValid && p.IsBot))
        {
            var copyPlayer = player;
            float rand = Random.Shared.NextSingle();

            if (rand < 0.06f)
            {
            }
            else if (rand < 0.53f)
            {
                AddTimer(0.4f, () =>
                {
                    if (!copyPlayer.IsValid) return;
                    Swap(copyPlayer, "weapon_aug", "weapon_m4a1");
                });
            }
            else
            {
                AddTimer(0.4f, () =>
                {
                    if (!copyPlayer.IsValid) return;
                    Swap(copyPlayer, "weapon_aug", "weapon_m4a1_silencer");
                });
            }
        }
        // Swap P90
        AddTimer(0.4f, () =>
        {
            foreach (var p in allPlayers)
            {
                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                var weapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (weapon == null || weapon.DesignerName != "weapon_p90") continue;

                float roll = Random.Shared.NextSingle();
                if (roll < 0.3f) Swap(p, "weapon_p90", "weapon_bizon");
                else if (roll < 0.4f) Swap(p, "weapon_p90", "weapon_mp7");
                else if (roll < 0.5f) Swap(p, "weapon_p90", "weapon_mp5sd");
                else if (roll < 0.6f) Swap(p, "weapon_p90", "weapon_ump45");
            }
        });
        // Swap XM1014
        AddTimer(0.4f, () =>
        {
            foreach (var p in allPlayers)
            {
                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                var weapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (weapon == null || weapon.DesignerName != "weapon_xm1014") continue;

                float roll = Random.Shared.NextSingle();
                if (roll < 0.5f)
                {
                    Swap(p, "weapon_xm1014", "weapon_negev");
                }
                else if (p.Team == CsTeam.CounterTerrorist && roll < 0.6f)
                {
                    Swap(p, "weapon_xm1014", "weapon_mag7");
                }
                else if (p.Team == CsTeam.Terrorist && roll < 0.65f)
                {
                    Swap(p, "weapon_xm1014", "weapon_sawedoff");
                }
            }
        });
        // Swap SSG08
        AddTimer(0.4f, () =>
        {
            if (ConVar.Find("sv_gravity")?.GetPrimitiveValue<float>() == 230f) return;
            foreach (var p in allPlayers)
            {
                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.WeaponServices == null) continue;

                var weapon = pawn.WeaponServices.ActiveWeapon.Value;
                if (weapon == null || weapon.DesignerName != "weapon_ssg08") continue;

                float roll = Random.Shared.NextSingle();

                if (roll < 0.05f)
                {
                    if (p.Team == CsTeam.CounterTerrorist)
                    {
                        Refund(p, "weapon_usp_silencer");
                        Refund(p, "weapon_hkp2000");
                    }
                    else
                    {
                        Refund(p, "weapon_glock");
                    }
                    Swap(p, "weapon_ssg08", "weapon_deagle");
                }
                else if (roll < 0.45f)
                {
                    if (p.Team == CsTeam.Terrorist)
                        Swap(p, "weapon_ssg08", "weapon_mac10");
                    else
                        Swap(p, "weapon_ssg08", "weapon_mp9");
                }
            }
        });
        // Big Advantage
        AddTimer(0.6f, () =>
        {
            if (!IsFirstRoundOfHalf())
            {
                foreach (var p in allPlayers)
                {
                    if (p.InGameMoneyServices == null || p.InGameMoneyServices.Account < 5200)
                        continue;

                    var pawn = p.PlayerPawn.Value;
                    if (pawn == null || !pawn.IsValid)
                        continue;

                    var weaponServices = pawn.WeaponServices;
                    if (weaponServices == null)
                        continue;

                    var activeWeapon = weaponServices.ActiveWeapon.Value;
                    if (activeWeapon == null)
                        continue;

                    var currentWeapon = activeWeapon.DesignerName;
                    if (string.IsNullOrEmpty(currentWeapon))
                        continue;

                    float roll = Random.Shared.NextSingle();

                    if (roll < 0.10f)
                    {
                        string newGun = p.Team == CsTeam.CounterTerrorist ? "weapon_scar20" : "weapon_g3sg1";
                        Swap(p, currentWeapon, newGun);
                    }
                    else if (roll < 0.14f)
                    {
                        Swap(p, currentWeapon, "weapon_m249");
                    }
                }
            }
        });
        // Buy Defuser
        AddTimer(3.0f, () =>
        {
            foreach (var p in allCT)
            {
                if (!p.IsValid) continue;
                if (p.InGameMoneyServices == null) continue;

                bool isPoor = _poorPlayersByTeam[CsTeam.CounterTerrorist].Contains(p);
                // Don't buy defuser if poor // Exception: pistol round with 500 left
                if (isPoor && !(IsFirstRoundOfHalf() && p.InGameMoneyServices.Account == 500))
                    continue;

                if (p.InGameMoneyServices.Account < 400)
                    continue;

                var pawn = p.PlayerPawn.Value;
                if (pawn == null || !pawn.IsValid || pawn.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
                    continue;

                var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
                if (itemServices.HasDefuser)
                    continue;

                Buy(p, "item_defuser");
            }
        });
        // Don't buy Armor if it's above 40
        AddTimer(1.0f, () =>
        {
            if (!IsFirstRoundOfHalf())
            {
                foreach (var p in allPlayers)
                {
                    if (!p.IsValid || p.PlayerPawn.Value == null) continue;

                    var pawn = p.PlayerPawn.Value;
                    var (_, _, prevArmor) = PreviousInventory(p);

                    if (pawn.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
                        continue;
                    var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);

                    int currentArmor = pawn.ArmorValue;

                    if (prevArmor > 40 && prevArmor <= 99 && currentArmor > 99 && itemServices.HasHelmet)
                    {
                        Refund(p, "item_assaultsuit");
                        p.GiveNamedItem("item_assaultsuit");
                        ref int armorValue = ref pawn.ArmorValue;
                        armorValue = prevArmor;
                        Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
                    }
                }
            }
        });
        // Special Rounds Buy Assistant
        AddTimer(0.4f, () =>
        {
            if (IsFirstRoundOfHalf())
            {
                foreach (var p in allPlayers)
                {
                    if (!p.IsValid || p.InGameMoneyServices == null) continue;
                    int money = p.InGameMoneyServices.Account;
                    float r = Random.Shared.NextSingle();

                    // Comp Pistol Rounds
                    if (money == 800)
                    {
                        if (p.Team == CsTeam.CounterTerrorist)
                        {
                            if (r < 0.50f) Buy(p, "item_kevlar");    // 50%
                            else if (r < 0.65f) { Swap(p, "weapon_usp_silencer", "weapon_elite"); Swap(p, "weapon_hkp2000", "weapon_elite"); } // 15%
                            else if (r < 0.75f) { Swap(p, "weapon_usp_silencer", "weapon_p250"); Swap(p, "weapon_hkp2000", "weapon_p250"); }   // 10%
                            else if (r < 0.83f) { Swap(p, "weapon_usp_silencer", "weapon_deagle"); Swap(p, "weapon_hkp2000", "weapon_deagle"); } // 8%
                            else if (r < 0.91f) { Swap(p, "weapon_usp_silencer", "weapon_cz75a"); Swap(p, "weapon_hkp2000", "weapon_cz75a"); }   // 8%
                            else if (r < 0.98f) { Swap(p, "weapon_usp_silencer", "weapon_fiveseven"); Swap(p, "weapon_hkp2000", "weapon_fiveseven"); } //7%
                            else if (r < 1.00f) { Swap(p, "weapon_usp_silencer", "weapon_revolver"); Swap(p, "weapon_hkp2000", "weapon_revolver"); } //2%
                        }
                        else
                        {
                            if (r < 0.50f) Buy(p, "item_kevlar");    // 50%
                            else if (r < 0.65f) Swap(p, "weapon_glock", "weapon_elite"); //15%
                            else if (r < 0.77f) Swap(p, "weapon_glock", "weapon_p250");  //12%
                            else if (r < 0.85f) Swap(p, "weapon_glock", "weapon_deagle");//8%
                            else if (r < 0.87f) Swap(p, "weapon_glock", "weapon_revolver");//2%
                            else if (r < 1.00f) Swap(p, "weapon_glock", "weapon_tec9");//13%
                        }
                    }

                    // Casual Pistol Rounds
                    else if (money == 1000)
                    {
                        if (p.Team == CsTeam.CounterTerrorist)
                        {
                            if (r < 0.20f) { Swap(p, "weapon_usp_silencer", "weapon_elite"); Swap(p, "weapon_hkp2000", "weapon_elite"); } //20%
                            else if (r < 0.50f) { Swap(p, "weapon_usp_silencer", "weapon_deagle"); Swap(p, "weapon_hkp2000", "weapon_deagle"); } //30%
                            else if (r < 0.65f) { Swap(p, "weapon_usp_silencer", "weapon_cz75a"); Swap(p, "weapon_hkp2000", "weapon_cz75a"); } //15%
                            else if (r < 0.95f) { Swap(p, "weapon_usp_silencer", "weapon_fiveseven"); Swap(p, "weapon_hkp2000", "weapon_fiveseven"); } //30%
                            else if (r < 1.00f) { Swap(p, "weapon_usp_silencer", "weapon_revolver"); Swap(p, "weapon_hkp2000", "weapon_revolver"); } //5%
                        }
                        else
                        {
                            if (r < 0.20f) Swap(p, "weapon_glock", "weapon_elite"); //20%
                            else if (r < 0.30f) Swap(p, "weapon_glock", "weapon_p250"); //10%
                            else if (r < 0.55f) Swap(p, "weapon_glock", "weapon_deagle");//25%
                            else if (r < 0.60f) Swap(p, "weapon_glock", "weapon_revolver");//5%
                            else if (r < 1.00f) Swap(p, "weapon_glock", "weapon_tec9");//40%
                        }
                    }

                    // First Round in OT
                    else if (money == 10000)
                    {
                        Buy(p, "item_assaultsuit");

                        if (p.Team == CsTeam.CounterTerrorist)
                        {
                            if (r < 0.35f) Buy(p, "weapon_m4a1");
                            else if (r < 0.70f) Buy(p, "weapon_m4a1_silencer");
                            else if (r < 0.90f) Buy(p, "weapon_awp");
                            else if (r < 1.00f) Buy(p, "weapon_scar20");
                        }
                        else
                        {
                            if (r < 0.70f) Buy(p, "weapon_ak47");
                            else if (r < 0.90f) Buy(p, "weapon_awp");
                            else if (r < 1.00f) Buy(p, "weapon_g3sg1");
                        }
                    }
                }
            }
        });
    }

    // Checks whether the bot already owns a primary weapon
    private bool HasPrimaryWeapon(CCSPlayerController player)
    {
        if (!player.IsValid || player.PlayerPawn.Value == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn.WeaponServices == null)
            return false;

        return pawn.WeaponServices.MyWeapons.Any(handle =>
            handle.Value != null && IsPrimaryWeaponName(handle.Value.DesignerName) &&
            handle.Value.DesignerName != "weapon_deagle");
    }

    //----------------------------------------------------------------------------------------------
    // Gives one legacy item and charges its configured price
    private bool Buy(CCSPlayerController player, string itemName)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        int money = player.InGameMoneyServices.Account;
        bool isCT = player.Team == CsTeam.CounterTerrorist;
        bool isT = player.Team == CsTeam.Terrorist;
        int price = 0;
        bool canBuy = true;
        int armor = pawn.ArmorValue;

        switch (itemName)
        {
            case "item_kevlar": price = 650; break;
            case "item_assaultsuit": price = armor > 99 ? 350 : 1000; break;

            case "item_defuser": price = 400; canBuy = isCT; break;
            case "weapon_taser": price = 200; break;

            case "weapon_glock": canBuy = isT; break;
            case "weapon_hkp2000": canBuy = isCT; break;
            case "weapon_usp_silencer": canBuy = isCT; break;
            case "weapon_elite": price = 300; break;
            case "weapon_p250": price = 300; break;
            case "weapon_tec9": price = 500; canBuy = isT; break;
            case "weapon_fiveseven": price = 500; canBuy = isCT; break;
            case "weapon_deagle": price = 700; break;
            case "weapon_cz75a": price = 500; break;
            case "weapon_revolver": price = 600; break;

            case "weapon_mac10": price = 1050; canBuy = isT; break;
            case "weapon_mp9": price = 1250; canBuy = isCT; break;
            case "weapon_mp7": price = 1500; break;
            case "weapon_mp5sd": price = 1500; break;
            case "weapon_ump45": price = 1200; break;
            case "weapon_bizon": price = 1400; break;
            case "weapon_p90": price = 2350; break;

            case "weapon_nova": price = 1050; break;
            case "weapon_xm1014": price = 2000; break;
            case "weapon_sawedoff": price = 1100; canBuy = isT; break;
            case "weapon_mag7": price = 1300; canBuy = isCT; break;

            case "weapon_galilar": price = 1800; canBuy = isT; break;
            case "weapon_ak47": price = 2700; canBuy = isT; break;
            case "weapon_sg556": price = 3000; canBuy = isT; break;
            case "weapon_famas": price = 1950; canBuy = isCT; break;
            case "weapon_m4a1": price = 2900; canBuy = isCT; break;
            case "weapon_m4a1_silencer": price = 2900; canBuy = isCT; break;
            case "weapon_aug": price = 3300; canBuy = isCT; break;

            case "weapon_ssg08": price = 1700; break;
            case "weapon_awp": price = 4750; break;
            case "weapon_scar20": price = 5000; canBuy = isCT; break;
            case "weapon_g3sg1": price = 5000; canBuy = isT; break;

            case "weapon_negev": price = 1700; break;
            case "weapon_m249": price = 5200; break;

            default: canBuy = false; break;
        }

        if (!canBuy)
        {
            Logger.LogDebug(
                "Buy rejected slot={Slot} team={Team} item={Item} reason=team_restriction",
                player.Slot,
                player.Team,
                itemName);
            return false;
        }

        if (money < price)
        {
            Logger.LogDebug(
                "Buy rejected slot={Slot} team={Team} item={Item} reason=insufficient_money money={Money} price={Price}",
                player.Slot,
                player.Team,
                itemName,
                money,
                price);
            return false;
        }

        player.GiveNamedItem(itemName);
        player.InGameMoneyServices.Account -= price;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");

        Logger.LogInformation(
            "Direct buy slot={Slot} team={Team} item={Item} moneyBefore={MoneyBefore} moneyAfter={MoneyAfter}",
            player.Slot,
            player.Team,
            itemName,
            money,
            player.InGameMoneyServices.Account);

        return true;
    }

    // Removes one legacy item and refunds its configured price
    private bool Refund(CCSPlayerController player, string itemName)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        if (!CanRefund(player, itemName))
            return false;

        bool hasItem = false;
        int price = 0;
        bool isCT = player.Team == CsTeam.CounterTerrorist;
        bool isT = player.Team == CsTeam.Terrorist;

        if (itemName.StartsWith("weapon_"))
        {
            hasItem = pawn.WeaponServices != null && pawn.WeaponServices.MyWeapons
                .Any(w => w.Value != null && w.Value.DesignerName == itemName);
        }
        else if (itemName == "item_assaultsuit" || itemName == "item_kevlar")
        {
            hasItem = pawn.ArmorValue > 0;
        }

        if (!hasItem)
            return false;

        bool canRefund = true;

        switch (itemName)
        {
            case "item_kevlar": price = 650; break;
            case "item_assaultsuit": price = 1000; break;

            case "weapon_taser": price = 200; break;

            case "weapon_glock": canRefund = isT; break;
            case "weapon_hkp2000": canRefund = isCT; break;
            case "weapon_usp_silencer": canRefund = isCT; break;
            case "weapon_elite": price = 300; break;
            case "weapon_p250": price = 300; break;
            case "weapon_tec9": price = 500; canRefund = isT; break;
            case "weapon_fiveseven": price = 500; canRefund = isCT; break;
            case "weapon_deagle": price = 700; break;
            case "weapon_cz75a": price = 500; break;
            case "weapon_revolver": price = 600; break;

            case "weapon_mac10": price = 1050; canRefund = isT; break;
            case "weapon_mp9": price = 1250; canRefund = isCT; break;
            case "weapon_mp7": price = 1500; break;
            case "weapon_mp5sd": price = 1500; break;
            case "weapon_ump45": price = 1200; break;
            case "weapon_bizon": price = 1400; break;
            case "weapon_p90": price = 2350; break;

            case "weapon_nova": price = 1050; break;
            case "weapon_xm1014": price = 2000; break;
            case "weapon_sawedoff": price = 1100; canRefund = isT; break;
            case "weapon_mag7": price = 1300; canRefund = isCT; break;

            case "weapon_galilar": price = 1800; canRefund = isT; break;
            case "weapon_ak47": price = 2700; canRefund = isT; break;
            case "weapon_sg556": price = 3000; canRefund = isT; break;
            case "weapon_famas": price = 1950; canRefund = isCT; break;
            case "weapon_m4a1": price = 2900; canRefund = isCT; break;
            case "weapon_m4a1_silencer": price = 2900; canRefund = isCT; break;
            case "weapon_aug": price = 3300; canRefund = isCT; break;

            case "weapon_ssg08": price = 1700; break;
            case "weapon_awp": price = 4750; break;
            case "weapon_scar20": price = 5000; canRefund = isCT; break;
            case "weapon_g3sg1": price = 5000; canRefund = isT; break;

            case "weapon_negev": price = 1700; break;
            case "weapon_m249": price = 5200; break;

            default: return false;
        }

        if (!canRefund)
            return false;

        if (itemName.StartsWith("weapon_"))
        {
            player.RemoveItemByDesignerName(itemName);
        }
        else if (itemName == "item_assaultsuit" || itemName == "item_kevlar")
        {
            pawn.ArmorValue = 0;
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_ArmorValue");
        }

        player.InGameMoneyServices.Account += price;
        // Cap at mp_maxmoney
        int maxMoney = ConVar.Find("mp_maxmoney")?.GetPrimitiveValue<int>() ?? 16000;
        if (player.InGameMoneyServices.Account > maxMoney)
            player.InGameMoneyServices.Account = maxMoney;
        Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");

        Logger.LogInformation(
            "Refund slot={Slot} team={Team} item={Item} price={Price} moneyAfter={MoneyAfter}",
            player.Slot,
            player.Team,
            itemName,
            price,
            player.InGameMoneyServices.Account);

        return true;
    }

    // Checks whether a legacy item was not retained from the previous round
    private bool CanRefund(CCSPlayerController player, string itemName)
    {
        if (IsFirstRoundOfHalf())
            return true;

        if (!player.IsValid || !player.IsBot)
            return false;

        // Check refund restrictions
        var (prevWeapons, _, _) = PreviousInventory(player);
        return !prevWeapons.Contains(itemName);
    }

    // Refunds one legacy item and buys its replacement
    private bool Swap(CCSPlayerController player, string oldItem, string newItem)
    {
        if (!player.IsValid || !player.IsBot || player.InGameMoneyServices == null)
            return false;

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid)
            return false;

        if (!Refund(player, oldItem))
            return false;

        if (!Buy(player, newItem))
        {
            Buy(player, oldItem);
            return false;
        }

        Logger.LogInformation(
            "Swap completed slot={Slot} team={Team} oldItem={OldItem} newItem={NewItem}",
            player.Slot,
            player.Team,
            oldItem,
            newItem);
        return true;
    }
}
