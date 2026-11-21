using System.Runtime.InteropServices;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotBuyPatch;

internal sealed class NativeLoadout : IDisposable
{
    private readonly MemoryFunctionWithReturn<nint, int, int, nint> _lookup;
    private readonly MemoryFunctionWithReturn<nint> _system;
    private readonly nint _inventoryVtable;
    private readonly int _inventoryOffset;
    // CSS cannot bind ushort.
    private readonly MemoryFunctionWithReturn<nint, int, int, int, nint> _stock;
    private readonly LoadoutSlots _slots = new();
    private readonly Dictionary<nint, CCSPlayerController> _uspBots = [];
    private nint _usp;

    internal NativeLoadout(string gamedataPath)
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || !(OperatingSystem.IsWindows() || OperatingSystem.IsLinux()))
            throw new PlatformNotSupportedException("Bot loadout requires Windows or Linux x64");
        var path = Addresses.ServerPath;
        var data = Gamedata.Load(gamedataPath, OperatingSystem.IsWindows() ? "windows" : "linux", File.ReadAllBytes(path));
        _inventoryOffset = data.InventoryOffset;
        if (_inventoryOffset > Schema.GetClassSize("CCSPlayerController_InventoryServices") - IntPtr.Size)
            throw new InvalidDataException("Player inventory offset is outside inventory services");
        _inventoryVtable = NativeAPI.FindVirtualTable(path, "CCSPlayerInventory");

        var calls = NativeAPI.FindSignature(path, data.StockCalls);
        var accessor = NativeAPI.FindSignature(path, data.SystemGetter);
        var stock = NativeAPI.FindSignature(path, data.Stock);
        if (_inventoryVtable == 0 || calls == 0 || accessor == 0 || stock == 0 ||
            CallTarget(calls) != accessor || CallTarget(calls + data.LastCallOffset) != stock)
            throw new InvalidDataException("Stock call site or inventory type did not resolve");
        _system = new(data.SystemGetter);
        _lookup = new(data.Lookup);
        _stock = new(data.Stock);
        if (_lookup.Handle == 0 || _system.Handle == 0 || _stock.Handle == 0) throw new InvalidOperationException("Missing loadout binding");
        _lookup.Hook(OnLookup, HookMode.Post);
    }

    private static nint CallTarget(nint call) => Marshal.ReadByte(call) == 0xE8
        ? call + 5 + Marshal.ReadInt32(call, 1)
        : throw new InvalidDataException("Expected CALL rel32");

    private nint Inventory(CCSPlayerController bot)
    {
        if (bot.InventoryServices is not { Handle: not 0 } services) return 0;
        nint inventory = services.Handle + _inventoryOffset;
        return Marshal.ReadIntPtr(inventory) == _inventoryVtable ? inventory : 0;
    }

    internal void ResetStartingPistols() => _uspBots.Clear();

    internal void RollStartingPistols()
    {
        foreach (var bot in Utilities.GetPlayers())
        {
            if (!bot.IsBot || bot.IsHLTV || Random.Shared.Next(5) == 0) continue;
            var inventory = Inventory(bot);
            if (inventory != 0) _uspBots[inventory] = bot;
        }
        _usp = _uspBots.Count == 0 ? 0 : Stock(61);
    }

    internal BuySlot? Prepare(CCSPlayerController bot, Gun gun)
    {
        var inventory = Inventory(bot);
        if (inventory == 0) throw new InvalidOperationException("Missing bot inventory");
        return _slots.Prepare(inventory, bot.TeamNum, gun,
            slot => Definition(_lookup.Invoke(inventory, bot.TeamNum, slot, bypasshook: false)), Stock);
    }

    private nint Stock(ushort definition)
    {
        var view = _stock.Invoke(_system.Invoke(), definition, 0, 0);
        return Definition(view) == definition ? view : 0;
    }

    private static ushort? Definition(nint view) =>
        view != 0 && new CEconItemView(view) is { Initialized: true } item ? item.ItemDefinitionIndex : null;

    private HookResult OnLookup(DynamicHook hook)
    {
        if (_slots.Empty && _uspBots.Count == 0) return HookResult.Continue;
        var slot = new BuySlot(hook.GetParam<nint>(0), hook.GetParam<int>(1), hook.GetParam<int>(2));
        var extra = _slots.View(slot);
        var usp = slot.IsCtStarter && _uspBots.TryGetValue(slot.Inventory, out var bot) &&
            bot.IsValid && bot.Team == CsTeam.CounterTerrorist && !bot.ControllingBot && !bot.HasBeenControlledByPlayerThisRound ? _usp : 0;
        if (extra == 0 && usp == 0) return HookResult.Continue;
        var view = slot.Override(Definition(hook.GetReturn<nint>()), extra, usp);
        if (view == 0) return HookResult.Continue;
        hook.SetReturn(view);
        return HookResult.Handled;
    }

    internal void Remove(BuySlot? slot) => _slots.Remove(slot);
    public void Dispose() => _lookup.Unhook(OnLookup, HookMode.Post);
}
