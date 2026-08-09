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
    // Detects the opening round of regulation, the second half, or overtime
    private bool IsFirstRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            int otMaxRounds = ConVar.Find("mp_overtime_maxrounds")?.GetPrimitiveValue<int>() ?? 6;

            if (maxRounds <= 0) maxRounds = 24;
            if (otMaxRounds <= 0) otMaxRounds = 6;

            int half = maxRounds / 2;
            int otHalf = otMaxRounds / 2;

            return played == 0
                || played == half
                || played == maxRounds
                || (played > maxRounds && (played - maxRounds) % otHalf == 0);
        }
        catch
        {
            return false;
        }
    }

    // Detects the penultimate round of the active half for the force-buy exception
    private bool IsSecondToLastRoundOfHalf()
    {
        try
        {
            var gameRules = Utilities
                .FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules")
                .FirstOrDefault()?.GameRules;

            if (gameRules == null)
                return false;

            int played = gameRules.TotalRoundsPlayed;
            int maxRounds = ConVar.Find("mp_maxrounds")?.GetPrimitiveValue<int>() ?? 24;
            if (maxRounds <= 0) maxRounds = 24;
            int half = maxRounds / 2;

            return played == half - 2 || played == maxRounds - 2;
        }
        catch { return false; }
    }

    // Returns the stable inventory-history index assigned to one bot user id
    private int GetBotIndex(CCSPlayerController player)
    {
        if (!player.IsValid || !IsControllerDemoBot(player)) return -1;
        int userId = player.UserId ?? -1;
        if (userId == -1) return -1;

        if (_botUserIdToIndex.TryGetValue(userId, out int idx)) return idx;
        int newIdx = ++_botIndexCounter;
        _botUserIdToIndex[userId] = newIdx;
        return newIdx;
    }

    // Removes one disconnected bot and its retained inventory history
    private void RemoveBot(CCSPlayerController player)
    {
        if (player == null) return;
        int userId = player.UserId ?? -1;
        if (userId == -1) return;

        if (_botUserIdToIndex.Remove(userId, out int idx))
        {
            _prevWeapons.Remove(idx);
            _prevMoney.Remove(idx);
            _prevArmor.Remove(idx);
        }
    }

    // Saves the managed bot inventory snapshot at the end of a round
    private void SavePreviousInventory()
    {
        if (IsFirstRoundOfHalf())
        {
            _prevWeapons.Clear();
            _prevMoney.Clear();
            _prevArmor.Clear();
            return;
        }

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !IsControllerDemoBot(player)) continue;
            int idx = GetBotIndex(player);
            if (idx == -1) continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid) continue;

            List<string> weapons = new();
            if (pawn.WeaponServices != null)
            {
                foreach (var wHandle in pawn.WeaponServices.MyWeapons)
                {
                    var w = wHandle.Value;
                    if (w == null) continue;
                    string name = w.DesignerName;
                    if (name == "item_kevlar" || name == "item_assaultsuit" || name == "item_defuser") continue;
                    weapons.Add(name);
                }
            }

            int money = player.InGameMoneyServices?.Account ?? 0;
            int armor = pawn.ArmorValue;

            _prevWeapons[idx] = weapons;
            _prevMoney[idx] = money;
            _prevArmor[idx] = armor;
        }
    }

    // Clears one dead bot's snapshot so newly spawned items remain refundable
    private void ClearPreviousInventory(CCSPlayerController player)
    {
        if (!player.IsValid || !IsControllerDemoBot(player))
            return;

        int idx = GetBotIndex(player);
        if (idx == -1) return;

        _prevWeapons.Remove(idx);
        _prevArmor.Remove(idx);
    }

    // Reads the current inventory values used by the legacy snapshot system
    private (List<string> Weapons, int Money, int Armor) Inventory(CCSPlayerController player)
    {
        List<string> weapons = new();
        int money = 0;
        int armor = 0;

        if (!player.IsValid || !IsControllerDemoBot(player)) return (weapons, money, armor);
        int idx = GetBotIndex(player);
        if (idx == -1) return (weapons, money, armor);

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid) return (weapons, money, armor);

        money = player.InGameMoneyServices?.Account ?? 0;
        armor = pawn.ArmorValue;

        if (pawn.WeaponServices != null)
        {
            foreach (var wHandle in pawn.WeaponServices.MyWeapons)
            {
                var w = wHandle.Value;
                if (w == null) continue;
                string name = w.DesignerName;
                if (name == "item_kevlar" || name == "item_assaultsuit" || name == "item_defuser") continue;
                weapons.Add(name);
            }
        }

        return (weapons, money, armor);
    }

    // Returns the previous-round inventory unless the current round resets refunds
    private (List<string> Weapons, int Money, int Armor) PreviousInventory(CCSPlayerController player)
    {
        List<string> weapons = new();
        int money = 0;
        int armor = 0;

        if (!player.IsValid || !IsControllerDemoBot(player)) return (weapons, money, armor);
        int idx = GetBotIndex(player);
        if (idx == -1) return (weapons, money, armor);

        if (IsFirstRoundOfHalf()) return (weapons, money, armor);

        if (_prevWeapons.TryGetValue(idx, out var w)) weapons = w;
        if (_prevMoney.TryGetValue(idx, out int m)) money = m;
        if (_prevArmor.TryGetValue(idx, out int a)) armor = a;

        return (weapons, money, armor);
    }
}
