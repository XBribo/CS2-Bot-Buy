namespace BotBuyPatch;

[Flags]
internal enum GunClass { Pistol = 1, Smg = 2, Rifle = 4, Shotgun = 8, Sniper = 16, MachineGun = 32, All = 63 }

internal sealed record Gun(ushort Id, string Alias, int Price, GunClass Class, char Team = '-', bool HardPick = false)
{
    public bool Primary => Class != GunClass.Pistol;
    internal bool WasBoughtOrHeld(IEnumerable<ushort> inventory, List<string> purchases) =>
        inventory.Contains(Id) || purchases.Contains("weapon_" + Alias) || purchases.Contains(Alias);
}

internal sealed record RoundOptions(bool Opening, bool Hard, bool RandomBuy, int EcoLimit, bool LowGravity,
    bool Defusers, GunClass Allowed, float GrenadeChance, float[] GrenadeWeights);

internal sealed record BuyDecision(string Aliases, Gun? BaseGun, Gun? Target);

internal static class RoundBuyPolicy
{
    internal static readonly Gun[] Guns =
    [
        new(1, "deagle", 700, GunClass.Pistol), new(2, "elite", 300, GunClass.Pistol),
        new(3, "fiveseven", 500, GunClass.Pistol, 'C'), new(4, "glock", 200, GunClass.Pistol, 'T'),
        new(30, "tec9", 500, GunClass.Pistol, 'T'), new(32, "hkp2000", 200, GunClass.Pistol, 'C'),
        new(36, "p250", 300, GunClass.Pistol), new(61, "usp_silencer", 200, GunClass.Pistol, 'C'),
        new(63, "cz75a", 500, GunClass.Pistol), new(64, "revolver", 600, GunClass.Pistol),
        new(7, "ak47", 2700, GunClass.Rifle, 'T', true), new(8, "aug", 3300, GunClass.Rifle, 'C', true),
        new(9, "awp", 4750, GunClass.Sniper, HardPick: true), new(10, "famas", 1950, GunClass.Rifle, 'C', true),
        new(11, "g3sg1", 5000, GunClass.Sniper, 'T', true), new(13, "galilar", 1800, GunClass.Rifle, 'T', true),
        new(14, "m249", 5200, GunClass.MachineGun), new(16, "m4a1", 2900, GunClass.Rifle, 'C', true),
        new(17, "mac10", 1050, GunClass.Smg, 'T'), new(19, "p90", 2350, GunClass.Smg),
        new(23, "mp5sd", 1500, GunClass.Smg), new(24, "ump45", 1200, GunClass.Smg),
        new(25, "xm1014", 2000, GunClass.Shotgun), new(26, "bizon", 1400, GunClass.Smg),
        new(27, "mag7", 1300, GunClass.Shotgun, 'C'), new(28, "negev", 1700, GunClass.MachineGun),
        new(29, "sawedoff", 1100, GunClass.Shotgun, 'T'), new(33, "mp7", 1500, GunClass.Smg),
        new(34, "mp9", 1250, GunClass.Smg, 'C'), new(35, "nova", 1050, GunClass.Shotgun),
        new(38, "scar20", 5000, GunClass.Sniper, 'C', true), new(39, "sg556", 3000, GunClass.Rifle, 'T', true),
        new(40, "ssg08", 1700, GunClass.Sniper), new(60, "m4a1_silencer", 2900, GunClass.Rifle, 'C')
    ];
    private static readonly Dictionary<ushort, Gun> ById = Guns.ToDictionary(g => g.Id);
    private static readonly Dictionary<string, Gun> ByAlias = Guns.ToDictionary(g => g.Alias);
    private static readonly ushort[] CtPool = [35, 25, 27, 34, 26, 33, 24, 19, 10, 40, 16, 8, 38, 9, 14, 28];
    private static readonly ushort[] TPool = [35, 25, 29, 17, 24, 19, 13, 7, 40, 39, 9, 11, 14, 28];

    internal static bool IsPrimary(ushort id) => ById.GetValueOrDefault(id)?.Primary == true;
    internal static Gun? FindGun(string alias) => ByAlias.GetValueOrDefault(alias);

    internal static BuyDecision Choose(bool ct, int money, HashSet<ushort> owned, int armor, bool helmet,
        bool kit, IReadOnlyList<ushort> prefs, RoundOptions rules, double teamRoll, Random rng)
    {
        int left = money;
        bool primary = owned.Any(IsPrimary);
        bool upgradedPistol = owned.Any(id => ById.GetValueOrDefault(id)?.Class == GunClass.Pistol && id is not (4 or 32 or 61));
        List<string> order = [];
        bool Legal(Gun g) => (g.Team == '-' || g.Team == (ct ? 'C' : 'T')) && rules.Allowed.HasFlag(g.Class);
        Gun? Available(string alias) => ByAlias.TryGetValue(alias, out var g) && Legal(g) && g.Price <= left ? g : null;
        Gun? Preferred(bool wantPrimary) => rules.RandomBuy ? null : prefs.Select(id => ById.GetValueOrDefault(id))
            .FirstOrDefault(g => g != null && g.Primary == wantPrimary && Legal(g) && g.Price <= left
                && (wantPrimary || !owned.Contains(g.Id)));
        void Add(string alias, int price) { if (left >= price) { order.Add(alias); left -= price; } }

        Gun? original = null, target = null;
        string? opening = rules.Opening ? OpeningBuyPolicy.Choose(ct, money, rng.NextSingle()) : null;
        if (opening == "vest")
            return new(armor == 0 ? "vest" : "", null, null);
        if (opening != null)
        {
            bool rifleRound = money == 10000;
            if (rifleRound && armor == 0) Add("vesthelm", 1000);
            if (!primary && (rifleRound || !upgradedPistol))
            {
                original = rifleRound ? Preferred(true) : null;
                original ??= Available(opening);
            }
        }
        else if (!primary && teamRoll < .20 && (!upgradedPistol || teamRoll >= .10))
        {
            original = teamRoll < .10 ? Available(ct ? "fiveseven" : "tec9") : Available(ct ? "mp9" : "mac10");
        }
        else if (money < rules.EcoLimit)
        {
            return new("", null, null);
        }
        else if (!primary)
        {
            original = Preferred(true);
            if (original == null)
            {
                var pool = (ct ? CtPool : TPool).Select(id => ById[id]).Where(Legal).ToArray();
                for (int attempt = 0; original == null && attempt < 7 && pool.Length > 0; attempt++)
                {
                    bool snipers = rules.RandomBuy || rng.NextDouble() < .5;
                    var candidates = pool.Where(g => snipers || g.Class != GunClass.Sniper).ToArray();
                    if (candidates.Length == 0) continue;
                    if (attempt == 0 && rules.Hard && !rules.RandomBuy && candidates.Any(g => g.HardPick))
                        candidates = candidates.Where(g => g.HardPick).ToArray();
                    var drawn = candidates[rng.Next(candidates.Length)];
                    if (drawn.Price <= left) original = drawn;
                }
            }
        }

        if (original != null)
        {
            var variant = Variant(original.Alias, ct, rules.LowGravity, rng.NextDouble());
            target = Available(variant) ?? original;
            if (!rules.Opening && target.Primary && left - target.Price >= 5200)
            {
                double r = rng.NextDouble();
                string? rich = r < .10 ? (ct ? "scar20" : "g3sg1") : r < .14 ? "m249" : null;
                if (rich != null) target = Available(rich) ?? target;
            }
            Add(target.Alias, target.Price);
        }

        if (opening == null)
        {
            if (!helmet && (armor <= 40 || armor == 100) && left >= (armor == 100 ? 350 : 1000))
                Add("vesthelm", armor == 100 ? 350 : 1000);
            else if (armor <= 40) Add("vest", 650);

            if (!primary && target == null && !upgradedPistol && rules.Allowed.HasFlag(GunClass.Pistol))
            {
                Gun? sidearm = Preferred(false);
                if (sidearm == null && rng.NextDouble() < (money >= 1000 ? .333 : .75))
                {
                    string[] pistols = ["p250", "deagle", ct ? "fiveseven" : "elite"];
                    sidearm = Available(pistols[rng.Next(3)]);
                }
                if (sidearm != null) { Add(sidearm.Alias, sidearm.Price); target = sidearm; }
            }
        }
        if (ct && !kit && rules.Defusers && (money >= 2800 || rules.Opening && left == 500)) Add("defuser", 400);
        if (opening == null && !owned.Any(id => id is >= 43 and <= 48) && rng.NextDouble() * 100 < rules.GrenadeChance)
        {
            string[] names = ["smokegrenade", "flashbang", "decoy", ct ? "incgrenade" : "molotov", "hegrenade"];
            int[] prices = [300, 200, 50, ct ? 500 : 400, 300];
            double pick = rng.NextDouble() * rules.GrenadeWeights.Sum();
            for (int i = 0; i < names.Length; i++)
                if ((pick -= rules.GrenadeWeights[i]) < 0) { Add(names[i], prices[i]); break; }
        }
        return new(string.Join(' ', order), original, target);
    }

    internal static string Variant(string gun, bool ct, bool lowGravity, double roll) => gun switch
    {
        "aug" => roll < .06 ? gun : roll < .53 ? "m4a1" : "m4a1_silencer",
        "p90" => roll < .30 ? "bizon" : roll < .40 ? "mp7" : roll < .50 ? "mp5sd" : roll < .60 ? "ump45" : gun,
        "xm1014" => roll < .50 ? "negev" : ct && roll < .60 ? "mag7" : !ct && roll < .65 ? "sawedoff" : gun,
        "ssg08" when !lowGravity => roll < .05 ? "deagle" : roll < .45 ? ct ? "mp9" : "mac10" : gun,
        _ => gun
    };
}
