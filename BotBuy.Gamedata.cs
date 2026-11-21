using System.Globalization;
using System.Text.Json;

namespace BotBuyPatch;

internal sealed record Gamedata(string Lookup, string StockCalls, int LastCallOffset, string SystemGetter, string Stock, int InventoryOffset)
{
    internal static Gamedata Load(string path, string platform, byte[] server)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowDuplicateProperties = false });
        var root = doc.RootElement;
        var offset = root.GetProperty("CCSPlayerController_InventoryServices::m_pInventory").GetProperty("offsets")
            .GetProperty(platform).GetInt32();
        if (offset < 0 || offset % IntPtr.Size != 0) throw new InvalidDataException("Invalid player inventory offset");

        string Read(string name)
        {
            var entry = root.GetProperty(name).GetProperty("signatures");
            if (entry.GetProperty("library").GetString() != "server") throw new InvalidDataException($"{name}: expected server");
            return RequireUnique(name, entry.GetProperty(platform).GetString(), server);
        }

        var calls = Read("StockItemCalls");
        const string call = "E8 ? ? ? ?";
        if (!calls.StartsWith(call + " ", StringComparison.Ordinal) || !calls.EndsWith(" " + call, StringComparison.Ordinal))
            throw new InvalidDataException("StockItemCalls must start and end with CALL rel32");
        return new(Read("CCSPlayerInventory::GetItemInLoadout"), calls, calls.Split(' ').Length - 5, Read("GetItemSystem"), Read("GetStockItemView"), offset);
    }

    private static byte?[] ParsePattern(string name, string? signature)
    {
        var tokens = (signature ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var pattern = new byte?[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i] == "?") continue;
            if (tokens[i].Length != 2 || !byte.TryParse(tokens[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte b))
                throw new InvalidDataException($"{name}: invalid signature token '{tokens[i]}'");
            pattern[i] = b;
        }
        if (!pattern.Any(b => b.HasValue)) throw new InvalidDataException($"{name}: empty or all-wildcard signature");
        return pattern;
    }

    private static bool Matches(ReadOnlySpan<byte> bytes, byte?[] pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
            if (pattern[i] is byte b && bytes[i] != b) return false;
        return true;
    }

    internal static string RequireUnique(string name, string? signature, ReadOnlySpan<byte> image)
    {
        var pattern = ParsePattern(name, signature);
        int anchor = Array.FindIndex(pattern, b => b.HasValue);
        int count = 0;
        while (image.Length >= pattern.Length)
        {
            int start = image.Slice(anchor, image.Length - pattern.Length + 1).IndexOf(pattern[anchor]!.Value);
            if (start < 0) break;
            if (Matches(image[start..], pattern) && ++count == 2) throw new InvalidDataException($"{name}: ambiguous signature");
            image = image[(start + 1)..];
        }
        if (count == 0) throw new InvalidDataException($"{name}: signature not found");
        return string.Join(' ', pattern.Select(b => b?.ToString("X2") ?? "?"));
    }
}
