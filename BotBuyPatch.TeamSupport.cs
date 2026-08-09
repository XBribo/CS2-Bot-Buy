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
    // Schedules weapon and armor support after individual bot purchases
    private void ScheduleTeamSupport(List<CCSPlayerController> allPlayers)
    {
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
    }
}

