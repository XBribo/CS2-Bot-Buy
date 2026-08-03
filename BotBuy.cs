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

public sealed class BotBuyPatch : BasePlugin
{
    public override string ModuleName => "BotBuyPatch";
    public override string ModuleVersion => "1.1.0";
    public override string ModuleAuthor => "ed0ard";
    public override string ModuleDescription => "Enable bots to take more buy options";

    private static readonly PluginCapability<IBotControllerApi> BotControllerCapability =
        new("botcontroller:api");

    private const float ControllerDemoRetryInterval = 0.1f;
    private const int ControllerDemoRetryTicks = 20;
    private const float RoundStartRetryInterval = 0.05f;
    private const int RoundStartRetryAttempts = 10;
    private const int DefaultBotEcoLimit = 2800;

    private Dictionary<int, int> _botUserIdToIndex = new();
    private int _botIndexCounter = 0;

    Dictionary<CsTeam, List<CCSPlayerController>> _poorPlayersByTeam = new();

    private Dictionary<int, List<string>> _prevWeapons = new();
    private Dictionary<int, int> _prevMoney = new();
    private Dictionary<int, int> _prevArmor = new();

    private IBotControllerApi? _botControllerApi;
    private CounterStrikeSharp.API.Modules.Timers.Timer? _controllerDemoTimer;
    private int _controllerDemoTicksRemaining;
    private int _roundStartRetriesRemaining;
    private bool _controllerDemoEnabled = true;
    private readonly Dictionary<int, ControllerPurchaseState> _controllerPurchaseStates = new();

    private sealed class ControllerPurchaseState
    {
        public int RoundIdentity { get; init; }
        public nint PawnHandle { get; init; }
        public List<ControllerPurchaseAction> Actions { get; init; } = new();
        public int NextActionIndex { get; set; }
        public int ConfirmedActions { get; set; }
        public int ConfirmationAttempts { get; set; }
        public bool ActionInFlight { get; set; }
        public bool ActionMutated { get; set; }
        public nint ActionEntityHandle { get; set; }
        public bool FirstActionFailed { get; init; }
        public string Source { get; init; } = string.Empty;
    }

    private sealed class ControllerPurchaseAction
    {
        public string ItemName { get; init; } = string.Empty;
        public string[] ReplacedItemNames { get; init; } = Array.Empty<string>();
        public int Price { get; init; }
    }

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

    // Gives one pending action and schedules its next-frame confirmation
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
            state.ConfirmationAttempts = 1;
            state.ActionMutated = action.ReplacedItemNames.Length > 0;
            state.ActionEntityHandle = nint.Zero;
            foreach (string replacedItemName in action.ReplacedItemNames)
                player.RemoveItemByDesignerName(replacedItemName);
            int actionIndex = state.NextActionIndex;
            if (action.ReplacedItemNames.Length > 0)
            {
                Server.NextFrame(() => GiveControllerPurchaseItem(player, roundIdentity, pawnHandle, actionIndex));
                return;
            }

            GiveControllerPurchaseItem(player, roundIdentity, pawnHandle, actionIndex);
        }
        catch (Exception exception)
        {
            state.ActionInFlight = false;
            RestoreControllerPurchaseAction(player, state, action);
            Logger.LogError(exception, "Controller direct equipment action failed slot={Slot} item={Item}", player.Slot, action.ItemName);
            HandleControllerPurchaseFailure(player, state, action);
        }
    }

    // Gives the pending item after a replacement has had one frame to settle
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
            Server.NextFrame(() => VerifyControllerPurchase(player, roundIdentity, pawnHandle, actionIndex));
        }
        catch (Exception exception)
        {
            state.ActionInFlight = false;
            RestoreControllerPurchaseAction(player, state, action);
            Logger.LogError(exception, "Controller direct equipment give failed slot={Slot} item={Item}", player.Slot, action.ItemName);
            HandleControllerPurchaseFailure(player, state, action);
        }
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
            if (state.ConfirmationAttempts < 3)
            {
                state.ConfirmationAttempts++;
                Server.NextFrame(() => VerifyControllerPurchase(player, roundIdentity, pawnHandle, actionIndex));
                return;
            }

            state.ActionInFlight = false;
            RestoreControllerPurchaseAction(player, state, action);
            HandleControllerPurchaseFailure(player, state, action);
            return;
        }

        if (player.InGameMoneyServices == null)
        {
            state.ActionInFlight = false;
            RestoreControllerPurchaseAction(player, state, action);
            HandleControllerPurchaseFailure(player, state, action);
            return;
        }

        state.ActionInFlight = false;
        state.ConfirmationAttempts = 0;
        player.InGameMoneyServices.Account -= action.Price;
        int maxMoney = ConVar.Find("mp_maxmoney")?.GetPrimitiveValue<int>() ?? 16000;
        if (player.InGameMoneyServices.Account > maxMoney)
            player.InGameMoneyServices.Account = maxMoney;
        if (player.InGameMoneyServices.Account < 0)
            player.InGameMoneyServices.Account = 0;
        PublishControllerMoney(player);
        state.ActionMutated = false;
        state.ActionEntityHandle = nint.Zero;
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

    // Restores an item removed by a failed replacement action
    private void RestoreControllerPurchaseAction(CCSPlayerController player, ControllerPurchaseState state, ControllerPurchaseAction action)
    {
        if (!state.ActionMutated)
            return;

        if (action.ItemName.StartsWith("weapon_", StringComparison.Ordinal))
            player.RemoveItemByDesignerName(action.ItemName);
        state.ActionMutated = false;
        state.ActionEntityHandle = nint.Zero;

        string[] replacedItems = action.ReplacedItemNames;
        if (replacedItems.Length == 0)
            return;

        Server.NextFrame(() =>
        {
            if (!player.IsValid)
                return;

            try
            {
                foreach (string replacedItemName in replacedItems)
                    player.GiveNamedItem(replacedItemName);
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "Controller direct equipment restore failed slot={Slot}", player.Slot);
            }
        });
    }

    // Clears only an unconfirmed chain, while preserving skip after partial success
    private void HandleControllerPurchaseFailure(CCSPlayerController player, ControllerPurchaseState state, ControllerPurchaseAction action)
    {
        if (action.ReplacedItemNames.Length > 0)
        {
            state.ConfirmationAttempts = 0;
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

    // Chooses a random affordable legal primary weapon from the bot profile or fallback pool
    private string? SelectControllerBaseWeapon(CCSPlayerController player, int money)
    {
        if (_botControllerApi != null && _botControllerApi.GetBotProfile(player.Slot, out var profile) && profile.WeaponPref != null)
        {
            int count = Math.Min(profile.WeaponPrefCount, profile.WeaponPref.Length);
            var preferredWeapons = new List<string>();
            for (int i = 0; i < count; i++)
            {
                if (!TryGetWeaponName(profile.WeaponPref[i], out string name) || !IsPrimaryWeaponName(name)) continue;
                if (IsWeaponAllowedForTeam(player.Team, name) && GetWeaponPrice(name) <= money)
                    preferredWeapons.Add(name);
            }
            if (preferredWeapons.Count > 0)
                return preferredWeapons[Random.Shared.Next(preferredWeapons.Count)];
        }

        string[] pool = player.Team == CsTeam.CounterTerrorist
            ? new[] { "weapon_m4a1", "weapon_m4a1_silencer", "weapon_aug", "weapon_famas", "weapon_mp9", "weapon_p90", "weapon_xm1014", "weapon_mag7", "weapon_nova", "weapon_ssg08", "weapon_awp", "weapon_scar20", "weapon_negev", "weapon_m249" }
            : new[] { "weapon_ak47", "weapon_galilar", "weapon_sg556", "weapon_mac10", "weapon_p90", "weapon_xm1014", "weapon_sawedoff", "weapon_nova", "weapon_ssg08", "weapon_awp", "weapon_g3sg1", "weapon_negev", "weapon_m249" };
        var affordable = pool.Where(name => GetWeaponPrice(name) <= money).ToArray();
        return affordable.Length == 0 ? null : affordable[Random.Shared.Next(affordable.Length)];
    }

    // Applies the requested in-memory weapon roll while preserving invalid outcomes
    private string RollControllerWeapon(CCSPlayerController player, string baseWeapon, int money)
    {
        float roll = Random.Shared.NextSingle();
        string? target = baseWeapon switch
        {
            "weapon_aug" => roll < 0.06f ? null : roll < 0.53f ? "weapon_m4a1" : "weapon_m4a1_silencer",
            "weapon_p90" => roll < 0.30f ? "weapon_bizon" : roll < 0.40f ? "weapon_mp7" : roll < 0.50f ? "weapon_mp5sd" : roll < 0.60f ? "weapon_ump45" : null,
            "weapon_xm1014" => roll < 0.50f ? "weapon_negev" : player.Team == CsTeam.CounterTerrorist && roll < 0.60f ? "weapon_mag7" : player.Team == CsTeam.Terrorist && roll < 0.65f ? "weapon_sawedoff" : null,
            "weapon_ssg08" when (ConVar.Find("sv_gravity")?.GetPrimitiveValue<float>() ?? 800f) != 230f => roll < 0.05f ? "weapon_deagle" : roll < 0.45f ? (player.Team == CsTeam.CounterTerrorist ? "weapon_mp9" : "weapon_mac10") : null,
            _ => null,
        };

        if (!IsFirstRoundOfHalf() && money >= 5200)
        {
            float advantageRoll = Random.Shared.NextSingle();
            if (advantageRoll < 0.10f)
                target = player.Team == CsTeam.CounterTerrorist ? "weapon_scar20" : "weapon_g3sg1";
            else if (advantageRoll < 0.14f)
                target = "weapon_m249";
        }

        return target != null && IsWeaponAllowedForTeam(player.Team, target) && GetWeaponPrice(target) <= money ? target : baseWeapon;
    }

    // Returns the active eco threshold while preserving the old half-end force-buy exception
    private int GetControllerEcoLimit()
    {
        if (IsSecondToLastRoundOfHalf())
            return 0;

        try
        {
            float configuredLimit = ConVar.Find("bot_eco_limit")?.GetPrimitiveValue<float>() ?? DefaultBotEcoLimit;
            if (float.IsNaN(configuredLimit) || float.IsInfinity(configuredLimit))
                return DefaultBotEcoLimit;

            return Math.Max(0, (int)MathF.Round(configuredLimit));
        }
        catch (Exception exception)
        {
            Logger.LogWarning(exception, "Failed to read bot_eco_limit; using default limit={DefaultLimit}", DefaultBotEcoLimit);
            return DefaultBotEcoLimit;
        }
    }

    // Maps a profile weapon definition index to its engine designer name
    private static bool TryGetWeaponName(int defIndex, out string name)
    {
        name = defIndex switch
        {
            1 => "weapon_deagle",
            7 => "weapon_ak47",
            8 => "weapon_aug",
            9 => "weapon_awp",
            10 => "weapon_famas",
            11 => "weapon_g3sg1",
            13 => "weapon_galilar",
            14 => "weapon_m249",
            16 => "weapon_m4a1",
            17 => "weapon_mac10",
            19 => "weapon_p90",
            23 => "weapon_mp5sd",
            24 => "weapon_ump45",
            25 => "weapon_xm1014",
            26 => "weapon_bizon",
            27 => "weapon_mag7",
            28 => "weapon_negev",
            29 => "weapon_sawedoff",
            33 => "weapon_mp7",
            34 => "weapon_mp9",
            35 => "weapon_nova",
            38 => "weapon_scar20",
            39 => "weapon_sg556",
            40 => "weapon_ssg08",
            60 => "weapon_m4a1_silencer",
            _ => string.Empty,
        };
        return !string.IsNullOrEmpty(name);
    }

    // Returns whether a designer name belongs to the primary weapon slot
    private static bool IsPrimaryWeaponName(string name) => name is
        "weapon_ak47" or "weapon_aug" or "weapon_awp" or "weapon_famas" or "weapon_g3sg1" or
        "weapon_galilar" or "weapon_m249" or "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_mac10" or
        "weapon_p90" or "weapon_mp5sd" or "weapon_ump45" or "weapon_xm1014" or "weapon_bizon" or
        "weapon_mag7" or "weapon_negev" or "weapon_sawedoff" or "weapon_mp7" or "weapon_mp9" or
        "weapon_nova" or "weapon_scar20" or "weapon_sg556" or "weapon_ssg08";

    // Returns whether a weapon is one of the free round-start pistols
    private static bool IsStarterPistolName(string name) => name is
        "weapon_glock" or "weapon_hkp2000" or "weapon_usp_silencer";

    // Returns whether a weapon is legal for the bot team
    private static bool IsWeaponAllowedForTeam(CsTeam team, string name) => name switch
    {
        "weapon_ak47" or "weapon_galilar" or "weapon_sg556" or "weapon_mac10" or "weapon_sawedoff" or "weapon_g3sg1" => team == CsTeam.Terrorist,
        "weapon_m4a1" or "weapon_m4a1_silencer" or "weapon_aug" or "weapon_famas" or "weapon_mp9" or "weapon_mag7" or "weapon_scar20" => team == CsTeam.CounterTerrorist,
        _ => team is CsTeam.CounterTerrorist or CsTeam.Terrorist,
    };

    // Returns the current buy price for a weapon designer name
    private static int GetWeaponPrice(string name) => name switch
    {
        "weapon_elite" => 300,
        "weapon_p250" => 300,
        "weapon_tec9" => 500,
        "weapon_fiveseven" => 500,
        "weapon_deagle" => 700,
        "weapon_cz75a" => 500,
        "weapon_revolver" => 600,
        "weapon_ak47" => 2700,
        "weapon_aug" => 3300,
        "weapon_awp" => 4750,
        "weapon_famas" => 1950,
        "weapon_g3sg1" => 5000,
        "weapon_galilar" => 1800,
        "weapon_m249" => 5200,
        "weapon_m4a1" => 2900,
        "weapon_mac10" => 1050,
        "weapon_p90" => 2350,
        "weapon_mp5sd" => 1500,
        "weapon_ump45" => 1200,
        "weapon_xm1014" => 2000,
        "weapon_bizon" => 1400,
        "weapon_mag7" => 1300,
        "weapon_negev" => 1700,
        "weapon_sawedoff" => 1100,
        "weapon_mp7" => 1500,
        "weapon_mp9" => 1250,
        "weapon_nova" => 1050,
        "weapon_scar20" => 5000,
        "weapon_sg556" => 3000,
        "weapon_ssg08" => 1700,
        "weapon_m4a1_silencer" => 2900,
        _ => 0,
    };

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

    [GameEventHandler]// Unlock Refund Restriction
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
        // Drop Weapons
        AddTimer(2.0f, () =>
        {
            if (!IsFirstRoundOfHalf())
            {
                foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
                {
                    if (!_poorPlayersByTeam.TryGetValue(team, out var poor))
                        poor = new List<CCSPlayerController>();
                    poor = poor.Where(p => p.IsValid && p.InGameMoneyServices != null).ToList();

                    var richBots = allPlayers.Where(p => p.IsValid && p.Team == team && IsControllerDemoBot(p) && p.InGameMoneyServices?.Account >= 2900).ToList();

                    if (poor.Count == 0 || richBots.Count == 0) continue;

                    var giftedPoor = new HashSet<CCSPlayerController>();

                    var shuffledPoor = poor.Where(p => !HasPrimaryWeapon(p)).OrderBy(_ => Random.Shared.Next()).ToList();
                    int poorIndex = 0;

                    foreach (var rich in richBots)
                    {
                        if (poorIndex >= shuffledPoor.Count) break;
                        if (rich.InGameMoneyServices == null) continue;

                        int richMoney = rich.InGameMoneyServices.Account;
                        int price = team == CsTeam.CounterTerrorist ? 2900 : 2700;

                        int maxGive = richMoney / price;
                        if (maxGive > 3) maxGive = 3;
                        if (maxGive <= 0) continue;

                        int given = 0;
                        while (given < maxGive && poorIndex < shuffledPoor.Count)
                        {
                            var poorPlayer = shuffledPoor[poorIndex];
                            poorIndex++;

                            if (!poorPlayer.IsValid || giftedPoor.Contains(poorPlayer)) continue;

                            string gun = team == CsTeam.CounterTerrorist
                                ? (Random.Shared.Next(2) == 0 ? "weapon_m4a1_silencer" : "weapon_m4a1")
                                : "weapon_ak47";
                            poorPlayer.GiveNamedItem(gun);
                            giftedPoor.Add(poorPlayer);

                            rich.InGameMoneyServices.Account -= price;
                            if (rich.InGameMoneyServices.Account < 0) rich.InGameMoneyServices.Account = 0;
                            Utilities.SetStateChanged(rich, "CCSPlayerController", "m_pInGameMoneyServices");

                            Logger.LogInformation(
                                "Weapon gift giverSlot={GiverSlot} targetSlot={TargetSlot} team={Team} item={Item} price={Price} giverMoneyAfter={MoneyAfter}",
                                rich.Slot,
                                poorPlayer.Slot,
                                team,
                                gun,
                                price,
                                rich.InGameMoneyServices.Account);

                            foreach (var teammate in allPlayers.Where(p => p.IsValid && p.Team == team))
                                teammate.PrintToChat($"{ChatColors.Green}{rich.PlayerName}{ChatColors.Yellow}: {poorPlayer.PlayerName}, I dropped a weapon for ya");
                            given++;
                        }
                    }
                }
            }
        });
        // Armor Gift Cycle: richest non-poor bot buys armor for a random unarmored teammate
        AddTimer(2.5f, () =>
        {
            foreach (var team in new[] { CsTeam.CounterTerrorist, CsTeam.Terrorist })
            {
                _poorPlayersByTeam.TryGetValue(team, out var poor);
                var poorSet = new HashSet<CCSPlayerController>(poor ?? new List<CCSPlayerController>());

                while (true)
                {
                    var needArmor = allPlayers
                        .Where(p => p.IsValid && IsControllerDemoBot(p) && p.Team == team
                            && HasPrimaryWeapon(p)
                            && (p.PlayerPawn.Value?.ArmorValue ?? 1) == 0)
                        .ToList();

                    if (needArmor.Count == 0) break;

                    var buyer = allPlayers
                        .Where(p => p.IsValid && IsControllerDemoBot(p) && p.Team == team
                            && !poorSet.Contains(p)
                            && p.InGameMoneyServices?.Account >= 650)
                        .OrderByDescending(p => p.InGameMoneyServices!.Account)
                        .FirstOrDefault();
                    // No one has enough money anymore
                    if (buyer == null) break;

                    var target = needArmor[Random.Shared.Next(needArmor.Count)];
                    if (!target.IsValid) continue;

                    int buyerMoney = buyer.InGameMoneyServices!.Account;
                    // Terrorist bots only buy full armor
                    if (team == CsTeam.Terrorist && buyerMoney < 1000) break;
                    string item = buyerMoney >= 1000 ? "item_assaultsuit" : "item_kevlar";
                    int price = buyerMoney >= 1000 ? 1000 : 650;

                    target.GiveNamedItem(item);
                    buyer.InGameMoneyServices.Account -= price;
                    Utilities.SetStateChanged(buyer, "CCSPlayerController", "m_pInGameMoneyServices");

                    Logger.LogInformation(
                        "Armor gift giverSlot={GiverSlot} targetSlot={TargetSlot} team={Team} item={Item} price={Price} giverMoneyAfter={MoneyAfter}",
                        buyer.Slot,
                        target.Slot,
                        team,
                        item,
                        price,
                        buyer.InGameMoneyServices.Account);
                }
            }
        });
        return;
    }

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

    private void ClearPreviousInventory(CCSPlayerController player)
    {
        if (!player.IsValid || !IsControllerDemoBot(player))
            return;

        int idx = GetBotIndex(player);
        if (idx == -1) return;

        _prevWeapons.Remove(idx);
        _prevArmor.Remove(idx);
    }

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
//----------------------------------------------------------------------------------------------
