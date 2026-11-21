namespace BotBuyPatch;

internal readonly record struct BuySlot(nint Inventory, int Team, int Position)
{
    internal bool IsCtStarter => Team == 3 && Position == LoadoutSlots.StartingPistol;
    internal nint Override(ushort? existing, nint extra, nint usp) =>
        IsCtStarter ? (existing is null or 32 ? usp : 0) : (existing == null ? extra : 0);
}

internal sealed class LoadoutSlots
{
    internal const int StartingPistol = 2;
    private readonly Dictionary<BuySlot, nint> _extra = [];

    internal bool Empty => _extra.Count == 0;
    internal nint View(BuySlot slot) => _extra.GetValueOrDefault(slot);
    internal void Remove(BuySlot? slot) { if (slot.HasValue) _extra.Remove(slot.Value); }

    internal BuySlot? Prepare(nint inventory, int team, Gun gun, Func<int, ushort?> read, Func<ushort, nint> stock)
    {
        int first = gun.Class switch { GunClass.Pistol => 2, GunClass.Rifle or GunClass.Sniper => 14, _ => 8 };
        int empty = -1;
        for (int i = 2; i <= 25; i++)
        {
            var id = read(i);
            if (id == gun.Id) return null;
            if (id == null && i != StartingPistol && empty < 0 && (i >= first && i < first + 6 || gun.Primary && i >= 20)) empty = i;
        }
        if (empty < 0) throw new InvalidOperationException($"No empty weapon slot for definition {gun.Id}");

        var slot = new BuySlot(inventory, team, empty);
        nint view = stock(gun.Id);
        if (view == 0) throw new InvalidOperationException($"No stock item view for definition {gun.Id}");
        _extra.Add(slot, view);
        try
        {
            if (read(empty) == gun.Id) return slot;
            throw new InvalidOperationException($"Loadout did not resolve definition {gun.Id} in slot {empty}");
        }
        catch { _extra.Remove(slot); throw; }
    }
}
