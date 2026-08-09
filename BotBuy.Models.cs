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
    private sealed class ControllerPurchaseState
    {
        public int RoundIdentity { get; init; }
        public nint PawnHandle { get; init; }
        public List<ControllerPurchaseAction> Actions { get; init; } = new();
        public int NextActionIndex { get; set; }
        public int ConfirmedActions { get; set; }
        public int RemovalAttempts { get; set; }
        public int ConfirmationAttempts { get; set; }
        public int RestorationAttempts { get; set; }
        public bool ActionInFlight { get; set; }
        public bool ActionMutated { get; set; }
        public nint ActionEntityHandle { get; set; }
        public List<string> ActionRemovedItemNames { get; } = new();
        public bool FirstActionFailed { get; init; }
        public string Source { get; init; } = string.Empty;
    }

    private sealed class ControllerPurchaseAction
    {
        public string ItemName { get; init; } = string.Empty;
        public string[] ReplacedItemNames { get; init; } = Array.Empty<string>();
        public int Price { get; init; }
    }
}
