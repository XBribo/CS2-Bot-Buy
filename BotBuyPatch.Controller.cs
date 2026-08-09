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
    // Resolves the optional BotController capability after all CSS plugins load
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        _botControllerApi = TryGetBotControllerApi();
        if (_botControllerApi == null)
        {
            Logger.LogWarning(
                "BotController API unavailable; using legacy purchase logic hotReload={HotReload}",
                hotReload);
        }
        else
        {
            Logger.LogInformation(
                "BotController API ready; automatic takeover is enabled hotReload={HotReload}",
                hotReload);
        }

        if (_botControllerApi != null && _controllerDemoEnabled)
            StartControllerDemoPurchaseWindow("all_plugins_loaded");
    }

    // Clears any native buy plans and stops the demo retry timer on unload
    public override void Unload(bool hotReload)
    {
        Logger.LogInformation("Unloading BotBuy hotReload={HotReload}", hotReload);
        ClearControllerDemoPlans();
    }

    // Resolves the BotController capability without making the plugin load fail
    private IBotControllerApi? TryGetBotControllerApi()
    {
        try
        {
            return BotControllerCapability.Get();
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Failed to resolve BotController API capability");
            return null;
        }
    }

    // Toggles the automatic BotController demo takeover from the server console
    [ConsoleCommand("css_botbuy_controller_demo", "Toggle the BotController buy-plan demo: 0 or 1")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnControllerDemoCommand(CCSPlayerController? player, CommandInfo command)
    {
        string value = command.ArgCount >= 2 ? command.GetArg(1) : string.Empty;
        if (value != "0" && value != "1")
        {
            command.ReplyToCommand($"[BotBuy] controller demo is {(_controllerDemoEnabled ? "on" : "off")}; use 0 or 1");
            Logger.LogInformation("Controller demo status queried enabled={Enabled}", _controllerDemoEnabled);
            return;
        }

        _controllerDemoEnabled = value == "1";
        if (!_controllerDemoEnabled)
            ClearControllerDemoPlans();
        else
            StartControllerDemoPurchaseWindow("console_command");

        command.ReplyToCommand($"[BotBuy] controller demo {(_controllerDemoEnabled ? "enabled" : "disabled")}");
        Logger.LogInformation("Controller demo toggled enabled={Enabled}", _controllerDemoEnabled);
    }

    // Starts the short retry window used to catch delayed bot/controller readiness
    private void StartControllerDemoPurchaseWindow(string source)
    {
        if (!_controllerDemoEnabled || _botControllerApi == null) return;
        if (IsControllerDemoPurchasesBlocked())
        {
            ClearControllerDemoPlans();
            return;
        }

        _controllerDemoTicksRemaining = Math.Max(
            _controllerDemoTicksRemaining,
            ControllerDemoRetryTicks);
        ApplyControllerDemoPlans(source);

        if (_controllerDemoTimer != null) return;
        Logger.LogInformation(
            "Started controller purchase readiness window source={Source} ticks={Ticks} interval={Interval}",
            source,
            ControllerDemoRetryTicks,
            ControllerDemoRetryInterval);
        _controllerDemoTimer = AddTimer(
            ControllerDemoRetryInterval,
            RunControllerDemoRetry,
            TimerFlags.REPEAT | TimerFlags.STOP_ON_MAPCHANGE);
    }

    // Applies the current retry tick and stops after the readiness window closes
    private void RunControllerDemoRetry()
    {
        ApplyControllerDemoPlans("retry_timer");
        _controllerDemoTicksRemaining--;
        if (_controllerDemoTicksRemaining > 0) return;

        _controllerDemoTimer?.Kill();
        _controllerDemoTimer = null;
        Logger.LogInformation("Controller purchase readiness window ended");
    }

    // Rebuilds the bot list for hot reload and manual enable recovery
    private void ApplyControllerDemoPlans(string source)
    {
        if (!_controllerDemoEnabled || _botControllerApi == null) return;
        if (IsControllerDemoPurchasesBlocked())
        {
            ClearControllerDemoPlans();
            return;
        }

        int scannedPlayers = 0;
        int newPurchases = 0;
        try
        {
            foreach (var player in Utilities.GetPlayers())
            {
                scannedPlayers++;
                if (ApplyControllerDemoPlan(player, source)) newPurchases++;
            }
        }
        catch (Exception exception) when (IsEntitySystemUnavailable(exception))
        {
            Logger.LogDebug(
                "Controller plan recovery scan deferred because the entity system is unavailable source={Source}",
                source);
            return;
        }

        if (source != "retry_timer" || newPurchases > 0)
        {
            Logger.LogInformation(
                "Controller purchase scan source={Source} players={Players} newPurchases={NewPurchases}",
                source,
                scannedPlayers,
                newPurchases);
        }
    }

    // Builds and starts one controller bot equipment chain without using a native buy plan
    private bool ApplyControllerDemoPlan(CCSPlayerController player, string source)
    {
        if (!_controllerDemoEnabled || _botControllerApi == null || !IsControllerDemoBot(player))
            return false;
        if (IsControllerDemoPurchasesBlocked())
        {
            ClearControllerDemoPlan(player.Slot);
            return false;
        }

        var pawn = player.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || player.InGameMoneyServices == null)
            return false;

        int roundIdentity = GetControllerRoundIdentity();
        if (roundIdentity < 0)
        {
            ClearControllerDemoPlan(player.Slot);
            return false;
        }
        nint pawnHandle = pawn.Handle;
        if (_controllerPurchaseStates.TryGetValue(player.Slot, out var state) &&
            state.RoundIdentity == roundIdentity && state.PawnHandle == pawnHandle)
            return false;

        try
        {
            if (!TryReadControllerEquipment(player, out var weapons, out int armor, out bool hasHelmet, out bool hasDefuser))
            {
                _botControllerApi.ClearBuyPlan(player.Slot);
                return false;
            }

            int money = player.InGameMoneyServices.Account;
            var actions = BuildControllerPurchaseActions(player, weapons, armor, hasHelmet, hasDefuser, money);
            if (actions.Count == 0)
            {
                _botControllerApi.ClearBuyPlan(player.Slot);
                _controllerPurchaseStates[player.Slot] = new ControllerPurchaseState
                {
                    RoundIdentity = roundIdentity,
                    PawnHandle = pawnHandle,
                    Source = source,
                    FirstActionFailed = false,
                };
                return false;
            }

            if (!_botControllerApi.SetBuySkip(player.Slot))
            {
                _botControllerApi.ClearBuyPlan(player.Slot);
                return false;
            }

            _controllerPurchaseStates[player.Slot] = new ControllerPurchaseState
            {
                RoundIdentity = roundIdentity,
                PawnHandle = pawnHandle,
                Actions = actions,
                Source = source,
            };
            ExecuteNextControllerPurchase(player, roundIdentity, pawnHandle);
            return true;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Controller direct equipment purchase failed slot={Slot}", player.Slot);
            _botControllerApi.ClearBuyPlan(player.Slot);
            _controllerPurchaseStates[player.Slot] = new ControllerPurchaseState
            {
                RoundIdentity = roundIdentity,
                PawnHandle = pawnHandle,
                Source = source,
                FirstActionFailed = true,
            };
            return false;
        }
    }


    // Recognizes native bots and BotHider-managed bots once their pawn is ready
    private static bool IsControllerDemoBot(CCSPlayerController player)
    {
        if (!player.IsValid) return false;
        if (player.IsBot) return true;

        var pawn = player.PlayerPawn.Value;
        return pawn != null && pawn.IsValid && pawn.Bot != null;
    }

    // Blocks controller purchases on maps or servers that provide their own loadout
    private static bool IsControllerDemoPurchasesBlocked()
    {
        if (Server.MapName == "aim_rush")
            return true;

        var botLoadout = ConVar.Find("bot_loadout");
        return botLoadout != null && !string.IsNullOrEmpty(botLoadout.StringValue);
    }

    // Identifies the transient CounterStrikeSharp map-transition entity error
    private static bool IsEntitySystemUnavailable(Exception exception)
    {
        return exception is NativeException &&
               exception.Message.Contains("Entity system yet is not initialized", StringComparison.Ordinal);
    }

    // Clears a disconnected slot's persisted BotController buy plan
    private void ClearControllerDemoPlan(int slot)
    {
        _controllerPurchaseStates.Remove(slot);
        try
        {
            _botControllerApi?.ClearBuyPlan(slot);
            Logger.LogDebug("Cleared controller buy plan slot={Slot}", slot);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to clear BotController buy plan slot={Slot}", slot);
        }
    }

    // Clears all persisted demo plans and stops the readiness retry window
    private void ClearControllerDemoPlans()
    {
        _controllerDemoTimer?.Kill();
        _controllerDemoTimer = null;
        _controllerDemoTicksRemaining = 0;
        int[] ownedSlots = _controllerPurchaseStates.Keys.ToArray();
        _controllerPurchaseStates.Clear();

        try
        {
            if (_botControllerApi == null)
            {
                Logger.LogDebug("No BotController plans to clear because the API is unavailable");
                return;
            }

            foreach (int slot in ownedSlots)
                _botControllerApi.ClearBuyPlan(slot);

            Logger.LogInformation("Cleared controller buy plans count={Count}", ownedSlots.Length);
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Failed to clear BotController buy plans count={Count}", ownedSlots.Length);
        }
    }
}

