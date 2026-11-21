namespace BotBuyPatch;

internal static class OpeningBuyPolicy
{
    internal static string? Choose(bool ct, int money, float roll) => (ct, money) switch
    {
        (true, 800) => roll switch { < .50f => "vest", < .65f => "elite", < .75f => "p250", < .83f => "deagle", < .91f => "cz75a", < .98f => "fiveseven", _ => "revolver" },
        (false, 800) => roll switch { < .50f => "vest", < .65f => "elite", < .77f => "p250", < .85f => "deagle", < .87f => "revolver", _ => "tec9" },
        (true, 1000) => roll switch { < .20f => "elite", < .50f => "deagle", < .65f => "cz75a", < .95f => "fiveseven", _ => "revolver" },
        (false, 1000) => roll switch { < .20f => "elite", < .30f => "p250", < .55f => "deagle", < .60f => "revolver", _ => "tec9" },
        (true, 10000) => roll switch { < .35f => "m4a1", < .70f => "m4a1_silencer", < .90f => "awp", _ => "scar20" },
        (false, 10000) => roll switch { < .70f => "ak47", < .90f => "awp", _ => "g3sg1" },
        _ => null
    };
}
