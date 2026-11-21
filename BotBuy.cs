using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotBuyPatch;

public sealed partial class BotBuyPatch : BasePlugin
{
    public override string ModuleName => "BotBuyPatch";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "ed0ard, unicbm & XBribo";
    public override string ModuleDescription => "Enable bots to take more buy options";

    [GameEventHandler]
    public HookResult OnRoundPrestart(EventRoundPrestart @event, GameEventInfo info)
    {
        _loadout?.ResetStartingPistols();
        if (BuysEnabled() && ConVar.Find("mp_ct_default_secondary")?.StringValue == "weapon_hkp2000")
            _loadout?.RollStartingPistols();
        return HookResult.Continue;
    }

    private bool BuysEnabled() => _controllerEnabled && Server.MapName is not ("aim_rush" or "rush_001")
        && string.IsNullOrEmpty(ConVar.Find("bot_loadout")?.StringValue);

    [GameEventHandler]
    public HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ResetControllerRound();
        if (!BuysEnabled()) return HookResult.Continue;

        var rules = Utilities.FindAllEntitiesByDesignerName<CCSGameRulesProxy>("cs_gamerules").FirstOrDefault()?.GameRules;
        if (rules == null || rules.WarmupPeriod) return HookResult.Continue;

        var players = Utilities.GetPlayers().Where(CanShop).ToList();

        int played = rules.TotalRoundsPlayed;
        int max = IntCvar("mp_maxrounds", 24);
        if (max < 2) max = 24;
        int otRounds = IntCvar("mp_overtime_maxrounds", 6);
        int otHalf = otRounds >= 2 ? otRounds / 2 : 3;
        bool opening = played == 0 || played == max / 2 || (played >= max && (played - max) % otHalf == 0);
        bool last = played + 1 == max / 2 || played + 1 == max || (played >= max && (played + 1 - max) % otHalf == 0);
        PrepareControllerPlans(players, opening, last, rules);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        int round = _roundGeneration;
        AddTimer(.25f, () => { if (round == _roundGeneration) ObserveControllerPurchases(); }, TimerFlags.STOP_ON_MAPCHANGE);
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        ResetControllerRound();
        return HookResult.Continue;
    }

    [GameEventHandler]
    public HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        if (@event.Userid is { IsValid: true, IsBot: true } player) ClearControllerPlan(player.Slot);
        return HookResult.Continue;
    }

    private static bool CanShop(CCSPlayerController p) => p.IsValid && !p.IsHLTV && p.PawnIsAlive
        && !p.ControllingBot && !p.HasBeenControlledByPlayerThisRound
        && p.Team is CsTeam.CounterTerrorist or CsTeam.Terrorist && p.InGameMoneyServices != null
        && p.PlayerPawn.Value is { IsValid: true, WeaponServices: not null };

    private static int IntCvar(string name, int fallback) => (int)FloatCvar(name, fallback);
    private static bool BoolCvar(string name, bool fallback) => FloatCvar(name, fallback ? 1 : 0) != 0;
    private static float FloatCvar(string name, float fallback)
    {
        var cvar = ConVar.Find(name);
        return cvar?.Type switch
        {
            ConVarType.Float32 => cvar.GetPrimitiveValue<float>(),
            ConVarType.Int32 => cvar.GetPrimitiveValue<int>(),
            ConVarType.Bool => cvar.GetPrimitiveValue<bool>() ? 1 : 0,
            _ => fallback
        };
    }
}
