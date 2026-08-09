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
}

