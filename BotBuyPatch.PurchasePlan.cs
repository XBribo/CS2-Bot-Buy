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
    // Reads the complete weapon and equipment state required to build a reliable chain
    private static bool TryReadControllerEquipment(CCSPlayerController player, out List<string> weapons, out int armor, out bool hasHelmet, out bool hasDefuser)
    {
        weapons = new List<string>();
        armor = 0;
        hasHelmet = false;
        hasDefuser = false;
        try
        {
            var pawn = player.PlayerPawn.Value;
            if (!player.IsValid || pawn == null || !pawn.IsValid || pawn.WeaponServices == null ||
                pawn.ItemServices == null || pawn.ItemServices.Handle == nint.Zero)
                return false;

            foreach (var handle in pawn.WeaponServices.MyWeapons)
            {
                var weapon = handle.Value;
                if (weapon != null) weapons.Add(weapon.DesignerName);
            }

            var itemServices = new CCSPlayer_ItemServices(pawn.ItemServices.Handle);
            armor = pawn.ArmorValue;
            hasHelmet = itemServices.HasHelmet;
            hasDefuser = itemServices.HasDefuser;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Creates the old BotBuy-style random purchase plan from one money snapshot
    private List<ControllerPurchaseAction> BuildControllerPurchaseActions(CCSPlayerController player, List<string> weapons, int armor, bool hasHelmet, bool hasDefuser, int money)
    {
        var actions = new List<ControllerPurchaseAction>();
        int remaining = money;
        bool firstRound = IsFirstRoundOfHalf();

        // The USP replacement is free and was intentionally allowed during eco rounds
        if (player.Team == CsTeam.CounterTerrorist &&
            Random.Shared.NextSingle() < 0.8f &&
            weapons.Contains("weapon_hkp2000") &&
            !weapons.Contains("weapon_usp_silencer") &&
            CanControllerRefundWeapon(player, "weapon_hkp2000"))
        {
            AddControllerReplacementAction(
                actions,
                weapons,
                "weapon_usp_silencer",
                new[] { "weapon_hkp2000" },
                ref remaining);
        }

        bool specialRound = firstRound && (money == 800 || money == 1000 || money == 10000);
        if (specialRound)
        {
            AddControllerFirstRoundActions(player, weapons, armor, money, actions, ref remaining);
            AddControllerDefuserAction(player, hasDefuser, money, firstRound, actions, ref remaining);
            return actions;
        }

        // Native bot_eco_limit behavior: do not spend in a normal eco round
        if (money < GetControllerEcoLimit())
            return actions;

        AddControllerPrimaryAction(player, weapons, actions, ref remaining);
        AddControllerForceBuyAction(player, weapons, actions, ref remaining);

        if (armor < 100)
        {
            if (remaining >= 1000 && !hasHelmet)
            {
                AddControllerAction(actions, weapons, "item_assaultsuit", 1000, ref remaining);
            }
            else if (remaining >= 650)
            {
                AddControllerAction(actions, weapons, "item_kevlar", 650, ref remaining);
            }
        }
        else if (!hasHelmet && remaining >= 350)
        {
            AddControllerAction(actions, weapons, "item_assaultsuit", 350, ref remaining);
        }

        AddControllerDefuserAction(player, hasDefuser, money, firstRound, actions, ref remaining);
        return actions;
    }

    // Adds the old competitive, casual, and overtime first-round branches
    private static void AddControllerFirstRoundActions(CCSPlayerController player, List<string> weapons, int armor, int money, List<ControllerPurchaseAction> actions, ref int remaining)
    {
        float roll = Random.Shared.NextSingle();
        if (money == 800)
        {
            if (player.Team == CsTeam.CounterTerrorist)
            {
                if (roll < 0.50f) AddControllerAction(actions, weapons, "item_kevlar", 650, ref remaining);
                else if (roll < 0.65f) AddControllerStarterSwap(actions, weapons, "weapon_elite", ref remaining);
                else if (roll < 0.75f) AddControllerStarterSwap(actions, weapons, "weapon_p250", ref remaining);
                else if (roll < 0.83f) AddControllerStarterSwap(actions, weapons, "weapon_deagle", ref remaining);
                else if (roll < 0.91f) AddControllerStarterSwap(actions, weapons, "weapon_cz75a", ref remaining);
                else if (roll < 0.98f) AddControllerStarterSwap(actions, weapons, "weapon_fiveseven", ref remaining);
                else AddControllerStarterSwap(actions, weapons, "weapon_revolver", ref remaining);
            }
            else
            {
                if (roll < 0.50f) AddControllerAction(actions, weapons, "item_kevlar", 650, ref remaining);
                else if (roll < 0.65f) AddControllerStarterSwap(actions, weapons, "weapon_elite", ref remaining);
                else if (roll < 0.77f) AddControllerStarterSwap(actions, weapons, "weapon_p250", ref remaining);
                else if (roll < 0.85f) AddControllerStarterSwap(actions, weapons, "weapon_deagle", ref remaining);
                else if (roll < 0.87f) AddControllerStarterSwap(actions, weapons, "weapon_revolver", ref remaining);
                else AddControllerStarterSwap(actions, weapons, "weapon_tec9", ref remaining);
            }
            return;
        }

        if (money == 1000)
        {
            if (player.Team == CsTeam.CounterTerrorist)
            {
                if (roll < 0.20f) AddControllerStarterSwap(actions, weapons, "weapon_elite", ref remaining);
                else if (roll < 0.50f) AddControllerStarterSwap(actions, weapons, "weapon_deagle", ref remaining);
                else if (roll < 0.65f) AddControllerStarterSwap(actions, weapons, "weapon_cz75a", ref remaining);
                else if (roll < 0.95f) AddControllerStarterSwap(actions, weapons, "weapon_fiveseven", ref remaining);
                else AddControllerStarterSwap(actions, weapons, "weapon_revolver", ref remaining);
            }
            else
            {
                if (roll < 0.20f) AddControllerStarterSwap(actions, weapons, "weapon_elite", ref remaining);
                else if (roll < 0.30f) AddControllerStarterSwap(actions, weapons, "weapon_p250", ref remaining);
                else if (roll < 0.55f) AddControllerStarterSwap(actions, weapons, "weapon_deagle", ref remaining);
                else if (roll < 0.60f) AddControllerStarterSwap(actions, weapons, "weapon_revolver", ref remaining);
                else AddControllerStarterSwap(actions, weapons, "weapon_tec9", ref remaining);
            }
            return;
        }

        AddControllerAction(
            actions,
            weapons,
            "item_assaultsuit",
            armor > 99 ? 350 : 1000,
            ref remaining);

        string primary = player.Team == CsTeam.CounterTerrorist
            ? roll < 0.35f ? "weapon_m4a1"
                : roll < 0.70f ? "weapon_m4a1_silencer"
                : roll < 0.90f ? "weapon_awp"
                : "weapon_scar20"
            : roll < 0.70f ? "weapon_ak47"
                : roll < 0.90f ? "weapon_awp"
                : "weapon_g3sg1";
        AddControllerAction(actions, weapons, primary, GetWeaponPrice(primary), ref remaining);
    }

    // Adds one primary weapon and applies the old replacement probabilities
    private void AddControllerPrimaryAction(CCSPlayerController player, List<string> weapons, List<ControllerPurchaseAction> actions, ref int remaining)
    {
        string? currentPrimary = weapons.FirstOrDefault(IsPrimaryWeaponName);
        if (currentPrimary == null)
        {
            string? baseWeapon = SelectControllerBaseWeapon(player, remaining);
            if (baseWeapon == null)
                return;

            string finalWeapon = RollControllerWeapon(player, baseWeapon, remaining);
            int weaponPrice = GetWeaponPrice(finalWeapon);
            if (weaponPrice > 0 && weaponPrice <= remaining)
            {
                var initialReplacements = finalWeapon == "weapon_deagle"
                    ? weapons.Where(IsStarterPistolName).ToArray()
                    : Array.Empty<string>();
                int initialPrice = weaponPrice - initialReplacements.Sum(GetWeaponPrice);
                AddControllerAction(actions, weapons, finalWeapon, initialPrice, ref remaining, initialReplacements);
            }
            return;
        }

        string replacement = RollControllerWeapon(player, currentPrimary, remaining);
        if (string.Equals(replacement, currentPrimary, StringComparison.Ordinal))
            return;
        if (!CanControllerRefundWeapon(player, currentPrimary))
            return;

        var replacedItems = new List<string> { currentPrimary };
        if (replacement == "weapon_deagle")
        {
            replacedItems.AddRange(weapons.Where(IsStarterPistolName)
                .Where(itemName => CanControllerRefundWeapon(player, itemName)));
        }

        int price = GetWeaponPrice(replacement) - replacedItems.Sum(GetWeaponPrice);
        AddControllerAction(actions, weapons, replacement, price, ref remaining, replacedItems);
    }

    // Applies the old team-wide force-buy probability to the current controller bot
    private void AddControllerForceBuyAction(CCSPlayerController player, List<string> weapons, List<ControllerPurchaseAction> actions, ref int remaining)
    {
        if (!IsControllerTeamInForceBuyRange(player))
            return;

        float roll = Random.Shared.NextSingle();
        if (player.Team == CsTeam.CounterTerrorist)
        {
            string? starter = weapons.FirstOrDefault(IsStarterPistolName);
            if (roll < 0.10f && starter != null && CanControllerRefundWeapon(player, starter))
                AddControllerStarterSwap(actions, weapons, "weapon_fiveseven", ref remaining);
            else if (roll < 0.20f && !weapons.Contains("weapon_mp9"))
                AddControllerAction(actions, weapons, "weapon_mp9", GetWeaponPrice("weapon_mp9"), ref remaining);
        }
        else
        {
            string? starter = weapons.FirstOrDefault(IsStarterPistolName);
            if (roll < 0.10f && starter != null && CanControllerRefundWeapon(player, starter))
                AddControllerStarterSwap(actions, weapons, "weapon_tec9", ref remaining);
            else if (roll < 0.20f && !weapons.Contains("weapon_mac10"))
                AddControllerAction(actions, weapons, "weapon_mac10", GetWeaponPrice("weapon_mac10"), ref remaining);
        }
    }

    // Adds the old defuser exception for rich bots and the 500-dollar pistol round
    private static void AddControllerDefuserAction(CCSPlayerController player, bool hasDefuser, int startingMoney, bool firstRound, List<ControllerPurchaseAction> actions, ref int remaining)
    {
        if (player.Team != CsTeam.CounterTerrorist || hasDefuser || remaining < 400)
            return;

        bool poorAtRoundStart = startingMoney < DefaultBotEcoLimit;
        if (!poorAtRoundStart || (firstRound && remaining == 500))
            AddControllerAction(actions, new List<string>(), "item_defuser", 400, ref remaining);
    }

    // Creates one action, updates the simulated balance, and mutates the local inventory
    private static bool AddControllerAction(List<ControllerPurchaseAction> actions, List<string> weapons, string itemName, int price, ref int remaining, IEnumerable<string>? replacedItems = null)
    {
        if (price > remaining)
            return false;

        string[] actualReplacements = (replacedItems ?? Array.Empty<string>())
            .Where(weapons.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (itemName.StartsWith("weapon_", StringComparison.Ordinal) && weapons.Contains(itemName))
            return false;

        actions.Add(new ControllerPurchaseAction
        {
            ItemName = itemName,
            ReplacedItemNames = actualReplacements,
            Price = price,
        });
        foreach (string replacedItem in actualReplacements)
            weapons.Remove(replacedItem);
        if (itemName.StartsWith("weapon_", StringComparison.Ordinal))
            weapons.Add(itemName);
        remaining -= price;
        return true;
    }

    // Replaces one starter pistol with a paid sidearm using the old refund-plus-buy price
    private static bool AddControllerStarterSwap(List<ControllerPurchaseAction> actions, List<string> weapons, string newWeapon, ref int remaining)
    {
        string? starter = weapons.FirstOrDefault(IsStarterPistolName);
        if (starter == null)
            return false;

        int price = GetWeaponPrice(newWeapon) - GetWeaponPrice(starter);
        return AddControllerAction(actions, weapons, newWeapon, price, ref remaining, new[] { starter });
    }

    // Replaces a specific old weapon while preserving its refund value
    private static bool AddControllerReplacementAction(List<ControllerPurchaseAction> actions, List<string> weapons, string newWeapon, IEnumerable<string> oldWeapons, ref int remaining)
    {
        string[] replacements = oldWeapons.Where(weapons.Contains).Distinct(StringComparer.Ordinal).ToArray();
        if (replacements.Length == 0)
            return false;

        int price = GetWeaponPrice(newWeapon) - replacements.Sum(GetWeaponPrice);
        return AddControllerAction(actions, weapons, newWeapon, price, ref remaining, replacements);
    }

    // Checks whether every managed bot on the team is in the old force-buy range
    private static bool IsControllerTeamInForceBuyRange(CCSPlayerController player)
    {
        try
        {
            var teamBots = Utilities.GetPlayers()
                .Where(candidate => candidate.IsValid && IsControllerDemoBot(candidate) && candidate.Team == player.Team)
                .ToList();
            return teamBots.Count > 0 && teamBots.All(candidate =>
                candidate.InGameMoneyServices != null &&
                candidate.InGameMoneyServices.Account > 1000 &&
                candidate.InGameMoneyServices.Account < DefaultBotEcoLimit);
        }
        catch
        {
            return false;
        }
    }

    // Preserves the old refund restriction for weapons carried from the previous round
    private bool CanControllerRefundWeapon(CCSPlayerController player, string weaponName)
    {
        if (IsFirstRoundOfHalf())
            return true;

        var (previousWeapons, _, _) = PreviousInventory(player);
        return !previousWeapons.Contains(weaponName);
    }
}
