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
    private const float ControllerRemovalPollInterval = 0.02f;
    private const int ControllerRemovalMaxAttempts = 15;
    private const float ControllerConfirmationPollInterval = 0.05f;
    private const int ControllerConfirmationMaxAttempts = 6;

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
}
