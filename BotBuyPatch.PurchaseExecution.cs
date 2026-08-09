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
    // Starts one pending action and waits for replacement sources to disappear before giving
    private void ExecuteNextControllerPurchase(CCSPlayerController player, int roundIdentity, nint pawnHandle)
    {
        if (!IsControllerPurchaseIdentityCurrent(player, roundIdentity, pawnHandle) ||
            !_controllerPurchaseStates.TryGetValue(player.Slot, out var state) ||
            state.RoundIdentity != roundIdentity || state.PawnHandle != pawnHandle)
            return;

        if (state.FirstActionFailed || state.ActionInFlight || state.NextActionIndex >= state.Actions.Count)
            return;

        var action = state.Actions[state.NextActionIndex];
        if (player.InGameMoneyServices == null || player.InGameMoneyServices.Account < action.Price)
        {
            HandleControllerPurchaseFailure(player, state, action);
            return;
        }

        try
        {
            if (IsControllerDemoPurchasesBlocked())
                return;

            state.ActionInFlight = true;
            state.RemovalAttempts = 0;
            state.ConfirmationAttempts = 0;
            state.RestorationAttempts = 0;
            state.ActionMutated = false;
            state.ActionEntityHandle = nint.Zero;
            state.ActionRemovedItemNames.Clear();
            int actionIndex = state.NextActionIndex;
            if (action.ReplacedItemNames.Length > 0)
            {
                if (!TryReadControllerEquipment(
                        player,
                        out var weapons,
                        out _,
                        out _,
                        out _) ||
                    action.ReplacedItemNames.Any(itemName => !weapons.Contains(itemName)))
                {
                    BeginControllerPurchaseFailure(player, state, action);
                    return;
                }

                foreach (string replacedItemName in action.ReplacedItemNames)
                {
                    if (player.RemoveItemByDesignerName(replacedItemName))
                        state.ActionRemovedItemNames.Add(replacedItemName);
                }
                state.ActionMutated = state.ActionRemovedItemNames.Count > 0;
                if (state.ActionRemovedItemNames.Count != action.ReplacedItemNames.Length)
                {
                    BeginControllerPurchaseFailure(player, state, action);
                    return;
                }

                ScheduleControllerRemovalCheck(player, roundIdentity, pawnHandle, actionIndex);
                return;
            }

            GiveControllerPurchaseItem(player, roundIdentity, pawnHandle, actionIndex);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Controller direct equipment action failed slot={Slot} item={Item}", player.Slot, action.ItemName);
            BeginControllerPurchaseFailure(player, state, action);
        }
    }

    // Schedules the next replacement-removal observation using real server time
    private void ScheduleControllerRemovalCheck(CCSPlayerController player, int roundIdentity, nint pawnHandle, int actionIndex)
    {
        AddTimer(
            ControllerRemovalPollInterval,
            () => WaitForControllerReplacedItemsToDisappear(player, roundIdentity, pawnHandle, actionIndex),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Waits until every scheduled replacement source is absent from the current inventory
    private void WaitForControllerReplacedItemsToDisappear(CCSPlayerController player, int roundIdentity, nint pawnHandle, int actionIndex)
    {
        if (!TryGetControllerActionState(player, roundIdentity, pawnHandle, actionIndex, out var state, out var action))
            return;

        if (AreControllerItemsAbsent(player, action.ReplacedItemNames))
        {
            state.RemovalAttempts = 0;
            GiveControllerPurchaseItem(player, roundIdentity, pawnHandle, actionIndex);
            return;
        }

        state.RemovalAttempts++;
        if (state.RemovalAttempts < ControllerRemovalMaxAttempts)
        {
            ScheduleControllerRemovalCheck(player, roundIdentity, pawnHandle, actionIndex);
            return;
        }

        Logger.LogWarning(
            "Controller replacement removal timed out slot={Slot} item={Item} attempts={Attempts}",
            player.Slot,
            action.ItemName,
            state.RemovalAttempts);
        BeginControllerPurchaseFailure(player, state, action);
    }

    // Gives the pending item after every replacement source has left the inventory
    private void GiveControllerPurchaseItem(CCSPlayerController player, int roundIdentity, nint pawnHandle, int actionIndex)
    {
        if (!IsControllerPurchaseIdentityCurrent(player, roundIdentity, pawnHandle) ||
            !_controllerPurchaseStates.TryGetValue(player.Slot, out var state) ||
            state.RoundIdentity != roundIdentity || state.PawnHandle != pawnHandle ||
            state.FirstActionFailed || !state.ActionInFlight || actionIndex != state.NextActionIndex ||
            actionIndex >= state.Actions.Count)
            return;

        var action = state.Actions[actionIndex];
        try
        {
            nint givenItemHandle = player.GiveNamedItem(action.ItemName);
            if (givenItemHandle == nint.Zero)
                throw new InvalidOperationException($"GiveNamedItem returned a null entity for {action.ItemName}");

            state.ActionEntityHandle = givenItemHandle;
            ScheduleControllerPurchaseVerification(player, roundIdentity, pawnHandle, actionIndex);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Controller direct equipment give failed slot={Slot} item={Item}", player.Slot, action.ItemName);
            BeginControllerPurchaseFailure(player, state, action);
        }
    }

    // Schedules the next item confirmation using a bounded real-time interval
    private void ScheduleControllerPurchaseVerification(CCSPlayerController player, int roundIdentity, nint pawnHandle, int actionIndex)
    {
        AddTimer(
            ControllerConfirmationPollInterval,
            () => VerifyControllerPurchase(player, roundIdentity, pawnHandle, actionIndex),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Confirms one action before charging it and advancing the chain
    private void VerifyControllerPurchase(CCSPlayerController player, int roundIdentity, nint pawnHandle, int actionIndex)
    {
        if (!IsControllerPurchaseIdentityCurrent(player, roundIdentity, pawnHandle) ||
            !_controllerPurchaseStates.TryGetValue(player.Slot, out var state) ||
            state.RoundIdentity != roundIdentity || state.PawnHandle != pawnHandle ||
            state.FirstActionFailed || actionIndex != state.NextActionIndex ||
            actionIndex >= state.Actions.Count || !state.ActionInFlight)
            return;

        var action = state.Actions[actionIndex];
        bool found = IsControllerPurchaseConfirmed(player, state, action);
        if (!found)
        {
            state.ConfirmationAttempts++;
            if (state.ConfirmationAttempts < ControllerConfirmationMaxAttempts)
            {
                ScheduleControllerPurchaseVerification(player, roundIdentity, pawnHandle, actionIndex);
                return;
            }

            Logger.LogWarning(
                "Controller purchase confirmation timed out slot={Slot} item={Item} attempts={Attempts}",
                player.Slot,
                action.ItemName,
                state.ConfirmationAttempts);
            BeginControllerPurchaseFailure(player, state, action);
            return;
        }

        if (player.InGameMoneyServices == null)
        {
            BeginControllerPurchaseFailure(player, state, action);
            return;
        }

        state.ActionInFlight = false;
        state.RemovalAttempts = 0;
        state.ConfirmationAttempts = 0;
        state.RestorationAttempts = 0;
        player.InGameMoneyServices.Account -= action.Price;
        int maxMoney = ConVar.Find("mp_maxmoney")?.GetPrimitiveValue<int>() ?? 16000;
        if (player.InGameMoneyServices.Account > maxMoney)
            player.InGameMoneyServices.Account = maxMoney;
        if (player.InGameMoneyServices.Account < 0)
            player.InGameMoneyServices.Account = 0;
        PublishControllerMoney(player);
        state.ActionMutated = false;
        state.ActionEntityHandle = nint.Zero;
        state.ActionRemovedItemNames.Clear();
        state.ConfirmedActions++;
        state.NextActionIndex++;
        Logger.LogInformation("Controller direct buy slot={Slot} team={Team} item={Item} source={Source}", player.Slot, player.Team, action.ItemName, state.Source);
        ExecuteNextControllerPurchase(player, roundIdentity, pawnHandle);
    }

    // Checks the item-specific state transition after GiveNamedItem
    private static bool IsControllerPurchaseConfirmed(CCSPlayerController player, ControllerPurchaseState state, ControllerPurchaseAction action)
    {
        if (!TryReadControllerEquipment(player, out var weapons, out int armor, out bool hasHelmet, out bool hasDefuser))
            return false;

        if (action.ItemName.StartsWith("weapon_", StringComparison.Ordinal))
        {
            if (weapons.Contains(action.ItemName))
                return true;

            if (state.ActionEntityHandle == nint.Zero)
                return false;

            try
            {
                var givenWeapon = new CBasePlayerWeapon(state.ActionEntityHandle);
                return givenWeapon.IsValid && givenWeapon.DesignerName == action.ItemName;
            }
            catch
            {
                return false;
            }
        }

        return action.ItemName switch
        {
            "item_kevlar" => armor >= 100,
            "item_assaultsuit" when action.Price == 350 => hasHelmet,
            "item_assaultsuit" => armor >= 100 && hasHelmet,
            "item_defuser" => hasDefuser,
            _ => false,
        };
    }

    // Publishes both the controller pointer and the nested account field immediately
    private void PublishControllerMoney(CCSPlayerController player)
    {
        if (player.InGameMoneyServices == null)
            return;

        try
        {
            Utilities.SetStateChanged(
                player,
                "CCSPlayerController_InGameMoneyServices",
                "m_iAccount");
            Utilities.SetStateChanged(player, "CCSPlayerController", "m_pInGameMoneyServices");
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Failed to publish controller money slot={Slot}", player.Slot);
        }
    }

    // Starts failure recovery without allowing the next action to race the rollback
    private void BeginControllerPurchaseFailure(CCSPlayerController player, ControllerPurchaseState state, ControllerPurchaseAction action)
    {
        state.RemovalAttempts = 0;
        state.ConfirmationAttempts = 0;
        if (!state.ActionMutated || state.ActionRemovedItemNames.Count == 0)
        {
            CompleteControllerPurchaseFailure(player, state, action);
            return;
        }

        if (action.ItemName.StartsWith("weapon_", StringComparison.Ordinal) &&
            !AreControllerItemsAbsent(player, new[] { action.ItemName }))
        {
            player.RemoveItemByDesignerName(action.ItemName);
        }

        state.RestorationAttempts = 0;
        if (AreControllerRestorationBlockersAbsent(player, state, action))
        {
            RestoreControllerRemovedItems(player, state, action);
            return;
        }

        ScheduleControllerRestorationCheck(player, state.RoundIdentity, state.PawnHandle, state.NextActionIndex);
    }

    // Schedules a rollback check after delayed entity removal has had time to progress
    private void ScheduleControllerRestorationCheck(CCSPlayerController player, int roundIdentity, nint pawnHandle, int actionIndex)
    {
        AddTimer(
            ControllerRemovalPollInterval,
            () => WaitForControllerRestorationSlot(player, roundIdentity, pawnHandle, actionIndex),
            TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Waits until neither the failed item nor any delayed old item remains in the inventory
    private void WaitForControllerRestorationSlot(CCSPlayerController player, int roundIdentity, nint pawnHandle, int actionIndex)
    {
        if (!TryGetControllerActionState(player, roundIdentity, pawnHandle, actionIndex, out var state, out var action))
            return;

        if (AreControllerRestorationBlockersAbsent(player, state, action))
        {
            RestoreControllerRemovedItems(player, state, action);
            return;
        }

        state.RestorationAttempts++;
        if (state.RestorationAttempts < ControllerRemovalMaxAttempts)
        {
            ScheduleControllerRestorationCheck(player, roundIdentity, pawnHandle, actionIndex);
            return;
        }

        Logger.LogError(
            "Controller replacement rollback timed out slot={Slot} item={Item} attempts={Attempts}",
            player.Slot,
            action.ItemName,
            state.RestorationAttempts);
        CompleteControllerPurchaseFailure(player, state, action);
    }

    // Checks whether all entities that could conflict with rollback have disappeared
    private static bool AreControllerRestorationBlockersAbsent(CCSPlayerController player, ControllerPurchaseState state, ControllerPurchaseAction action)
    {
        var blockerNames = new List<string>(state.ActionRemovedItemNames);
        if (action.ItemName.StartsWith("weapon_", StringComparison.Ordinal))
            blockerNames.Add(action.ItemName);
        return AreControllerItemsAbsent(player, blockerNames);
    }

    // Restores only the old items whose removal was actually scheduled by this action
    private void RestoreControllerRemovedItems(CCSPlayerController player, ControllerPurchaseState state, ControllerPurchaseAction action)
    {
        try
        {
            foreach (string replacedItemName in state.ActionRemovedItemNames)
            {
                nint restoredItemHandle = player.GiveNamedItem(replacedItemName);
                if (restoredItemHandle == nint.Zero)
                {
                    Logger.LogWarning(
                        "Controller replacement restore returned null slot={Slot} item={Item}",
                        player.Slot,
                        replacedItemName);
                }
            }
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Controller direct equipment restore failed slot={Slot}", player.Slot);
        }

        CompleteControllerPurchaseFailure(player, state, action);
    }

    // Resets the current action and applies the existing chain failure policy
    private void CompleteControllerPurchaseFailure(CCSPlayerController player, ControllerPurchaseState state, ControllerPurchaseAction action)
    {
        state.ActionInFlight = false;
        state.ActionMutated = false;
        state.ActionEntityHandle = nint.Zero;
        state.RemovalAttempts = 0;
        state.ConfirmationAttempts = 0;
        state.RestorationAttempts = 0;
        state.ActionRemovedItemNames.Clear();
        HandleControllerPurchaseFailure(player, state, action);
    }

    // Reads one live in-flight action while rejecting stale timer callbacks
    private bool TryGetControllerActionState(
        CCSPlayerController player,
        int roundIdentity,
        nint pawnHandle,
        int actionIndex,
        out ControllerPurchaseState state,
        out ControllerPurchaseAction action)
    {
        state = null!;
        action = null!;
        if (!IsControllerPurchaseIdentityCurrent(player, roundIdentity, pawnHandle) ||
            !_controllerPurchaseStates.TryGetValue(player.Slot, out var currentState) ||
            currentState.RoundIdentity != roundIdentity || currentState.PawnHandle != pawnHandle ||
            currentState.FirstActionFailed || !currentState.ActionInFlight ||
            actionIndex != currentState.NextActionIndex || actionIndex >= currentState.Actions.Count)
            return false;

        state = currentState;
        action = currentState.Actions[actionIndex];
        return true;
    }

    // Checks whether every named item is absent from the current weapon inventory
    private static bool AreControllerItemsAbsent(CCSPlayerController player, IEnumerable<string> itemNames)
    {
        if (!TryReadControllerEquipment(player, out var weapons, out _, out _, out _))
            return false;

        return itemNames.All(itemName => !weapons.Contains(itemName));
    }

    // Clears only an unconfirmed chain, while preserving skip after partial success
    private void HandleControllerPurchaseFailure(CCSPlayerController player, ControllerPurchaseState state, ControllerPurchaseAction action)
    {
        if (action.ReplacedItemNames.Length > 0)
        {
            state.RemovalAttempts = 0;
            state.ConfirmationAttempts = 0;
            state.RestorationAttempts = 0;
            state.NextActionIndex++;
            Logger.LogWarning(
                "Controller direct equipment replacement failed slot={Slot} item={Item}; continuing remaining actions",
                player.Slot,
                action.ItemName);
            ExecuteNextControllerPurchase(player, state.RoundIdentity, state.PawnHandle);
            return;
        }

        if (state.ConfirmedActions == 0)
        {
            _botControllerApi?.ClearBuyPlan(player.Slot);
            _controllerPurchaseStates[player.Slot] = new ControllerPurchaseState
            {
                RoundIdentity = state.RoundIdentity,
                PawnHandle = state.PawnHandle,
                Source = state.Source,
                FirstActionFailed = true,
            };
            Logger.LogWarning("Controller direct equipment first action failed slot={Slot} item={Item}; native buy restored", player.Slot, action.ItemName);
            return;
        }

        Logger.LogWarning("Controller direct equipment chain stopped slot={Slot} item={Item} confirmed={Confirmed}; buy skip retained", player.Slot, action.ItemName, state.ConfirmedActions);
    }

    // Returns the current game round identity used to reject stale callbacks
    private static int GetControllerRoundIdentity()
    {
        try
        {
            return Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules?.TotalRoundsPlayed ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    // Rejects callbacks whose round or pawn no longer matches the original chain
    private static bool IsControllerPurchaseIdentityCurrent(CCSPlayerController player, int roundIdentity, nint pawnHandle)
    {
        try
        {
            if (!player.IsValid)
                return false;

            var pawn = player.PlayerPawn.Value;
            int currentRoundIdentity = GetControllerRoundIdentity();
            return pawn != null && pawn.IsValid && currentRoundIdentity >= 0 &&
                   currentRoundIdentity == roundIdentity && pawn.Handle == pawnHandle;
        }
        catch
        {
            return false;
        }
    }
}
