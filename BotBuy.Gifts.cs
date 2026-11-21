using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;

namespace BotBuyPatch;

public sealed partial class BotBuyPatch
{
    private sealed class GiftMember(CCSPlayerController player)
    {
        internal CCSPlayerController Player { get; } = player;
        internal CsTeam Team { get; } = player.Team;
        internal uint Pawn { get; } = player.PlayerPawn.Raw;
        internal bool Poor { get; } = player.InGameMoneyServices!.Account < GiftPolicy.PoorLimit;
        internal int Money => Player.InGameMoneyServices!.Account;
        internal bool HasPrimary => WeaponIds(Player.PlayerPawn.Value!).Any(RoundBuyPolicy.IsPrimary);

        internal void Pay(int price)
        {
            Player.InGameMoneyServices!.Account -= price;
            Utilities.SetStateChanged(Player, "CCSPlayerController", "m_pInGameMoneyServices");
        }
    }

    private void ScheduleGifts(GiftMember[] roster, bool opening)
    {
        int round = _roundGeneration;
        void Run(Action<GiftMember[], bool> give)
        {
            if (round != _roundGeneration) return;
            foreach (var team in roster.Where(Available).GroupBy(m => m.Team))
                give(team.ToArray(), team.Key == CsTeam.CounterTerrorist);
        }
        bool Available(GiftMember m) => CanShop(m.Player) && m.Player.PlayerPawn.Raw == m.Pawn && m.Player.Team == m.Team
            && (!m.Player.IsBot || _controllerPlans.TryGetValue(m.Player.Slot, out var plan) && !plan.Api.IsReplaying(m.Player.Slot));

        if (!opening) AddTimer(2f, () => Run(GiveRifles), TimerFlags.STOP_ON_MAPCHANGE);
        AddTimer(2.5f, () => Run(GiveArmor), TimerFlags.STOP_ON_MAPCHANGE);
    }

    private static void GiveRifles(GiftMember[] team, bool ct)
    {
        var recipients = team.Where(m => m.Poor && !m.HasPrimary).ToArray();
        Random.Shared.Shuffle(recipients);
        int next = 0;
        foreach (var donor in team.Where(m => m.Player.IsBot))
        {
            int count = GiftPolicy.RifleCount(ct, donor.Money);
            while (count > 0 && next < recipients.Length)
            {
                var target = recipients[next++];
                if (target == donor) continue;
                var weapon = target.Player.GiveNamedItem<CCSWeaponBase>(GiftPolicy.Rifle(ct, Random.Shared));
                if (weapon is not { IsValid: true }) continue;

                donor.Pay(GiftPolicy.RiflePrice(ct));
                WeaponOwner.SetIfUnowned(weapon, donor.Player.SteamID);
                foreach (var member in team.Where(m => !m.Player.IsBot))
                    member.Player.PrintToChat($"[BotBuy] {donor.Player.PlayerName} dropped a rifle for {target.Player.PlayerName}.");
                count--;
            }
        }
    }

    private static void GiveArmor(GiftMember[] team, bool ct)
    {
        var bots = team.Where(m => m.Player.IsBot).ToArray();
        var recipients = bots.Where(m => m.HasPrimary && m.Player.PlayerPawn.Value!.ArmorValue == 0).ToArray();
        Random.Shared.Shuffle(recipients);
        foreach (var target in recipients)
        {
            var donor = bots.Where(m => !m.Poor).MaxBy(m => m.Money);
            if (donor == null || GiftPolicy.Armor(ct, donor.Money) is not { } armor) break;
            var pawn = target.Player.PlayerPawn.Value!;
            target.Player.GiveNamedItem(armor.Item);
            if (pawn.ArmorValue > 0) donor.Pay(armor.Price);
        }
    }

}
