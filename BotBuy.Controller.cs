using BotControllerApi;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Cvars;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace BotBuyPatch;

public sealed partial class BotBuyPatch
{
    private static readonly PluginCapability<IBotControllerApi> ControllerCapability = new("botcontroller:api");
    private readonly Dictionary<int, ControllerPlan> _controllerPlans = new();
    private NativeLoadout? _loadout;
    private bool _controllerEnabled = true;
    private int _roundGeneration;

    public override void Load(bool hotReload)
    {
        try { _loadout = new NativeLoadout(Path.Combine(ModuleDirectory, "gamedata.json")); }
        catch (Exception ex) { Logger.LogWarning("Loadout patch disabled: {Reason}", ex.Message); }
        RegisterListener<Listeners.OnMapStart>(_ => ResetControllerRound());
        RegisterListener<Listeners.OnMapEnd>(() => { ResetControllerRound(); _loadout?.ResetStartingPistols(); });
        RegisterListener<Listeners.OnClientDisconnect>(ClearControllerPlan);
    }

    public override void Unload(bool hotReload)
    {
        try { ResetControllerRound(); }
        finally { _loadout?.Dispose(); }
    }

    [ConsoleCommand("css_botbuy_controller", "Use BotBuy plans (1) or leave buying to the game (0) next round")]
    [CommandHelper(whoCanExecute: CommandUsage.SERVER_ONLY)]
    public void OnControllerCommand(CCSPlayerController? player, CommandInfo command)
    {
        string value = command.ArgCount == 2 ? command.GetArg(1) : "";
        if (value is "0" or "1") _controllerEnabled = value == "1";
        command.ReplyToCommand($"[BotBuy] next round: {(_controllerEnabled ? "BotController plans" : "native buying")}");
    }

    private void PrepareControllerPlans(List<CCSPlayerController> players, bool opening, bool last, CCSGameRules rules)
    {
        var bots = players.Where(p => p.IsBot).ToArray();
        if (bots.Length == 0) return;
        IBotControllerApi? api;
        try { api = ControllerCapability.Get(); }
        catch (Exception ex)
        {
            Logger.LogWarning("BotController unavailable: {Reason}", ex.Message);
            return;
        }
        if (api == null) return;

        GunClass allowed = 0;
        foreach (var (name, group) in new[] {
            ("bot_allow_pistols", GunClass.Pistol), ("bot_allow_sub_machine_guns", GunClass.Smg),
            ("bot_allow_rifles", GunClass.Rifle), ("bot_allow_shotguns", GunClass.Shotgun),
            ("bot_allow_snipers", GunClass.Sniper), ("bot_allow_machine_guns", GunClass.MachineGun) })
            if (ConVar<bool>.Find(name)?.Value ?? true) allowed |= group;

        bool online = ConVar<bool>.Find("game_online")?.Value ?? true;
        bool hard = ConVar<string>.Find("sv_mmqueue_reservation")?.Value.StartsWith('Q') != true
            && (online ? ConVar<int>.Find("bot_difficulty")?.Value >= 2
                : ConVar<int>.Find("custom_bot_difficulty")?.Value is 4 or 5);
        float grenadeChance = ConVar<bool>.Find("bot_allow_grenades")?.Value == false
            ? 0 : ConVar<float>.Find("sv_bot_buy_grenade_chance")?.Value ?? 33;
        var options = new RoundOptions(opening, hard, ConVar<bool>.Find("bot_randombuy")?.Value ?? false,
            last ? 0 : (int)(ConVar<float>.Find("bot_eco_limit")?.Value ?? 2000), ConVar<float>.Find("sv_gravity")?.Value == 230f,
            rules.MapHasBombTarget, allowed, grenadeChance,
            new[] { "smoke", "flash", "decoy", "molotov", "hegrenade" }
                .Select(n => Math.Max(0, ConVar<float>.Find($"sv_bot_buy_{n}_weight")?.Value ?? (n == "hegrenade" ? 6 : 1))).ToArray());

        List<(int Slot, ControllerPlan Plan)> orders = [];
        foreach (var team in bots.GroupBy(p => p.Team))
        {
            double teamRoll = team.All(p => p.InGameMoneyServices!.Account is > 1000 and < 2800)
                ? Random.Shared.NextDouble() : 1.0;
            foreach (var p in team)
            {
                try
                {
                    if (api.IsReplaying(p.Slot) || api.BuyPlanItemCount(p.Slot) >= 0) continue;
                    int money = p.InGameMoneyServices!.Account;
                    ushort[] prefs = [];
                    if (!(opening && money is 800 or 1000))
                    {
                        if (!api.GetBotProfile(p.Slot, out var profile))
                        {
                            Logger.LogWarning("Missing profile: slot={Slot}", p.Slot);
                            continue;
                        }
                        prefs = profile.WeaponPref.Take(profile.WeaponPrefCount).ToArray();
                    }
                    var pawn = p.PlayerPawn.Value!;
                    if (pawn.ItemServices is not { Handle: not 0 } items) continue;
                    var gear = new CCSPlayer_ItemServices(items.Handle);
                    var decision = RoundBuyPolicy.Choose(p.Team == CsTeam.CounterTerrorist, money, WeaponIds(pawn).ToHashSet(),
                        pawn.ArmorValue, gear.HasHelmet, gear.HasDefuser, prefs, options, teamRoll, Random.Shared);
                    bool accepted = opening || decision.Aliases.Length == 0 ? api.SetBuySkip(p.Slot)
                        : api.SetBuyPlan(p.Slot, decision.Aliases);
                    if (!accepted)
                    {
                        Logger.LogWarning("Plan rejected: slot={Slot} plan={Plan}", p.Slot, decision.Aliases);
                        continue;
                    }
                    var plan = new ControllerPlan(api, p.EntityHandle.Raw, p.PlayerPawn.Raw, money, decision);
                    _controllerPlans.Add(p.Slot, plan);
                    if (_loadout != null && decision.Target is { } target)
                    {
                        try
                        {
                            plan.ExtraSlot = _loadout.Prepare(p, target);
                            if (plan.ExtraSlot is { } extra)
                                Logger.LogDebug("Loadout: slot={Slot} pos={Position} def={Def}", p.Slot, extra.Position, target.Id);
                        }
                        catch (Exception ex) { Logger.LogWarning("Loadout failed: slot={Slot} def={Def}: {Reason}", p.Slot, target.Id, ex.Message); }
                    }
                    if (opening && decision.Aliases.Length != 0) orders.Add((p.Slot, plan));
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "Planning failed: slot={Slot}", p.Slot);
                    ClearControllerPlan(p.Slot);
                }
            }
        }
        var roster = players.Select(p => new GiftMember(p)).ToArray();
        void StartBuying()
        {
            foreach (var (slot, plan) in orders) SubmitOpeningOrder(slot, plan);
            ScheduleGifts(roster, opening);
        }
        if (opening) AfterTeamIntro(rules, StartBuying);
        else StartBuying();
    }

    private void AfterTeamIntro(CCSGameRules rules, Action ready)
    {
        int round = _roundGeneration;
        void SubmitAfterIntro()
        {
            if (round != _roundGeneration) return;
            if (rules.TeamIntroPeriod)
            {
                AddTimer(.1f, SubmitAfterIntro, TimerFlags.STOP_ON_MAPCHANGE);
                return;
            }
            ready();
        }
        Server.NextFrame(SubmitAfterIntro);
    }

    private void SubmitOpeningOrder(int slot, ControllerPlan plan)
    {
        if (!_controllerPlans.TryGetValue(slot, out var owned) || !ReferenceEquals(plan, owned)) return;
        try
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            if (player is not { IsValid: true, IsBot: true, PawnIsAlive: true, ControllingBot: false,
                HasBeenControlledByPlayerThisRound: false } || !plan.Matches(player) || plan.Api.IsReplaying(slot))
            {
                ClearControllerPlan(slot);
                return;
            }
            if (!plan.Api.ClearBuyPlan(slot))
            {
                Logger.LogWarning("Skip release failed: slot={Slot}", slot);
                return;
            }
            if (!plan.Api.SetRoundBuyPlan(slot, plan.Pawn, plan.Decision.Aliases))
            {
                _loadout?.Remove(plan.ExtraSlot);
                _controllerPlans.Remove(slot);
                Logger.LogWarning("Opening plan rejected: slot={Slot} plan={Plan}", slot, plan.Decision.Aliases);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Opening buy failed: slot={Slot}", slot);
            ClearControllerPlan(slot);
        }
    }

    private static IEnumerable<ushort> WeaponIds(CCSPlayerPawn pawn) => pawn.WeaponServices!.MyWeapons
        .Select(w => w.Value).Where(w => w is { IsValid: true }).Select(w => w!.AttributeManager.Item.ItemDefinitionIndex);

    [GameEventHandler]
    public HookResult OnItemPurchase(EventItemPurchase @event, GameEventInfo info)
    {
        if (@event.Userid is not { IsValid: true, IsBot: true } bot) return HookResult.Continue;
        var item = @event.Weapon;
        if (_controllerPlans.TryGetValue(bot.Slot, out var plan) && plan.Matches(bot))
            plan.Purchases.Add(item);
        WeaponOwner.OnPurchase(bot, item);
        return HookResult.Continue;
    }

    private void ObserveControllerPurchases()
    {
        foreach (var (slot, plan) in _controllerPlans)
        {
            if (plan.Decision.Target is not { } target) continue;
            var p = Utilities.GetPlayerFromSlot(slot);
            if (p is not { IsValid: true } || !plan.Matches(p) || p.PlayerPawn.Value is not { IsValid: true, WeaponServices: not null } pawn) continue;
            if (!target.WasBoughtOrHeld(WeaponIds(pawn), plan.Purchases))
                Logger.LogWarning("Gun missing: slot={Slot} gun={Gun} def={Def} bought=[{Bought}] cash={Before}->{After}",
                    slot, target.Alias, target.Id, string.Join(',', plan.Purchases), plan.Money, p.InGameMoneyServices?.Account);
        }
    }

    private void ResetControllerRound()
    {
        _roundGeneration++;
        foreach (int slot in _controllerPlans.Keys.ToArray()) ClearControllerPlan(slot);
    }

    private void ClearControllerPlan(int slot)
    {
        if (!_controllerPlans.Remove(slot, out var plan)) return;
        _loadout?.Remove(plan.ExtraSlot);
        try
        {
            if (!plan.Api.ClearBuyPlan(slot)) Logger.LogWarning("Cleanup rejected: slot={Slot}", slot);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "Cleanup failed: slot={Slot}", slot); }
    }

    private sealed record ControllerPlan(IBotControllerApi Api, uint Player, uint Pawn, int Money, BuyDecision Decision)
    {
        internal BuySlot? ExtraSlot { get; set; }
        internal List<string> Purchases { get; } = [];
        internal bool Matches(CCSPlayerController p) => Player == p.EntityHandle.Raw && Pawn == p.PlayerPawn.Raw;
    }
}
