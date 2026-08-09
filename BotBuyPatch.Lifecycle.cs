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
    //----------------------------------------------------------------------------------------------
    // Stores a persistent plan as soon as a player finishes connecting
    [GameEventHandler]
    public HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && IsControllerDemoBot(player))
        {
            GetBotIndex(player);
            Logger.LogInformation(
                "Bot connected slot={Slot} userid={UserId} team={Team} name='{Name}'",
                player.Slot,
                player.UserId ?? -1,
                player.Team,
                player.PlayerName);
        }

        if (player != null) ApplyControllerDemoPlan(player, "player_connect_full");
        return HookResult.Continue;
    }

    // Defers the persistent controller plan until the spawned pawn is initialized
    [GameEventHandler]
    public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid && IsControllerDemoBot(player))
        {
            Logger.LogInformation(
                "Bot spawned slot={Slot} team={Team} money={Money} pawnValid={PawnValid}",
                player.Slot,
                player.Team,
                player.InGameMoneyServices?.Account ?? -1,
                player.PlayerPawn.Value?.IsValid ?? false);
            Server.NextFrame(() =>
            {
                if (player.IsValid && IsControllerDemoBot(player))
                    ApplyControllerDemoPlan(player, "player_spawn");
            });
        }

        return HookResult.Continue;
    }

    // Clears purchase and inventory state when a player disconnects
    [GameEventHandler]
    public HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
        {
            Logger.LogInformation(
                "Player disconnect slot={Slot} userid={UserId} name='{Name}' isBot={IsBot}",
                player.Slot,
                player.UserId ?? -1,
                player.PlayerName,
                player.IsBot);
            ClearControllerDemoPlan(player.Slot);
            RemoveBot(player);
        }
        return HookResult.Continue;
    }

    // Clears retained inventory on death to unlock the refund restriction
    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null && player.IsValid && IsControllerDemoBot(player))
        {
            ClearPreviousInventory(player);
        }
        return HookResult.Continue;
    }

    // Saves the legacy inventory snapshot without reapplying persistent plans
    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        Logger.LogInformation("Round ended; saving bot inventory state");
        SavePreviousInventory();

        return HookResult.Continue;
    }
    //----------------------------------------------------------------------------------------------
    // Starts the legacy player workflow at the earliest safe frame
    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        Logger.LogInformation("Round start event received map={Map}", Server.MapName);

        _roundStartRetriesRemaining = RoundStartRetryAttempts;
        Server.NextFrame(ProcessRoundStart);
        return HookResult.Continue;
    }

    // Processes round-start purchases after the entity system becomes available
    private void ProcessRoundStart()
    {

        // Don't Buy on Aim_Rush
        if (Server.MapName == "aim_rush")
        {
            Logger.LogInformation("Skipping controller and legacy purchases on map={Map}", Server.MapName);
            ClearControllerDemoPlans();
            return;
        }

        List<CCSPlayerController> allPlayers = new();
        List<CCSPlayerController> allCT = new();
        List<CCSPlayerController> allT = new();
        List<CCSPlayerController> ctBots = new();
        List<CCSPlayerController> tBots = new();

        try
        {
            foreach (var player in Utilities.GetPlayers())
            {
                if (!player.IsValid) continue;
                allPlayers.Add(player);

                if (player.Team == CsTeam.CounterTerrorist)
                {
                    allCT.Add(player);
                    if (IsControllerDemoBot(player)) ctBots.Add(player);
                }
                else if (player.Team == CsTeam.Terrorist)
                {
                    allT.Add(player);
                    if (IsControllerDemoBot(player)) tBots.Add(player);
                }
            }
        }
        catch (Exception exception) when (IsEntitySystemUnavailable(exception))
        {
            if (_roundStartRetriesRemaining <= 0)
            {
                Logger.LogError(
                    exception,
                    "Round start player scan failed after entity readiness retries map={Map}",
                    Server.MapName);
                return;
            }

            _roundStartRetriesRemaining--;
            Logger.LogDebug(
                "Round start player scan deferred map={Map} retriesRemaining={RetriesRemaining}",
                Server.MapName,
                _roundStartRetriesRemaining);
            AddTimer(
                RoundStartRetryInterval,
                ProcessRoundStart,
                TimerFlags.STOP_ON_MAPCHANGE);
            return;
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Round start player scan failed map={Map}", Server.MapName);
            return;
        }
        // Drop Weapons
        _poorPlayersByTeam.Clear();
        var poorCT = allPlayers.Where(p => p.IsValid && p.Team == CsTeam.CounterTerrorist && p.InGameMoneyServices?.Account < 2800).ToList();
        var poorT = allPlayers.Where(p => p.IsValid && p.Team == CsTeam.Terrorist && p.InGameMoneyServices?.Account < 2800).ToList();
        _poorPlayersByTeam[CsTeam.CounterTerrorist] = poorCT;
        _poorPlayersByTeam[CsTeam.Terrorist] = poorT;

        ConVar? botLoadout = ConVar.Find("bot_loadout");
        if (botLoadout != null && !string.IsNullOrEmpty(botLoadout.StringValue))
        {
            Logger.LogWarning(
                "Skipping controller purchase plans because bot_loadout is set value='{Loadout}'",
                botLoadout.StringValue);
            ClearControllerDemoPlans();
            return;
        }

        bool useControllerDemo = _controllerDemoEnabled && _botControllerApi != null;
        Logger.LogInformation(
            "Round start map={Map} totalPlayers={Players} ctBots={CtBots} tBots={TBots} firstRoundOfHalf={FirstRound} controllerDemo={ControllerDemo}",
            Server.MapName,
            allPlayers.Count,
            ctBots.Count,
            tBots.Count,
            IsFirstRoundOfHalf(),
            useControllerDemo);
        if (useControllerDemo)
        {
            StartControllerDemoPurchaseWindow("round_start");
        }
        else
        {
            ScheduleLegacyPurchases(allPlayers, allCT, ctBots, tBots);
        }
        ScheduleTeamSupport(allPlayers);
        return;
    }

    // Updates the native bot economy limit when the freeze period ends
    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        ConVar? botLoadout = ConVar.Find("bot_loadout");
        if (botLoadout != null && !string.IsNullOrEmpty(botLoadout.StringValue))
        {
            return HookResult.Continue;
        }

        // Don't save money in the last round of each half
        var ecoLimitCvar = ConVar.Find("bot_eco_limit");
        if (ecoLimitCvar != null)
        {
            if (IsSecondToLastRoundOfHalf())
                Server.ExecuteCommand("bot_eco_limit 0");
            else
                Server.ExecuteCommand("bot_eco_limit 2800");
        }

        foreach (var player in Utilities.FindAllEntitiesByDesignerName<CCSPlayerController>("cs_player_controller"))
        {
            if (!player.IsValid || !player.IsBot)
                continue;

            var pawn = player.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            var bot = pawn.Bot;
            if (bot == null)
                continue;

            // Nothing here
        }
        return HookResult.Continue;
    }
    //----------------------------------------------------------------------------------------------
}
