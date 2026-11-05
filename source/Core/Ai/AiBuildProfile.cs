using System.Globalization;
using System.Text.Json;

namespace San14Tenkazu;

internal static class AiBuildProfile
{
    internal const string NovemberStock = "315F16D0F27CF511D200FAED112E1E82A0110C1ECB5600B5BE720A0307F86117";
    internal const ulong NovemberManager = 0x1B9D4F0;
    internal static bool IsNovember(GameIdentity identity) =>
        string.Equals(identity.ExeHash, GameNativeFactsNovember2026.ExeSha256, StringComparison.OrdinalIgnoreCase);

    private sealed record Mapping(int Node, uint Class, uint Block, int Original);
    private static readonly Lazy<IReadOnlyDictionary<string, Mapping>> November = new(() =>
    {
        using var stream = typeof(AiBuildProfile).Assembly.GetManifestResourceStream("Ai.November2026.json")
            ?? throw new InvalidDataException("Missing AI guide for the November executable");
        using var doc = JsonDocument.Parse(stream);
        uint Hex(JsonElement v) => uint.Parse(v.GetString()!, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return doc.RootElement.EnumerateObject().ToDictionary(row => row.Name,
            row => new Mapping(row.Value[0].GetInt32(), Hex(row.Value[1]), Hex(row.Value[2]), row.Value[3].GetInt32()));
    });

    internal static IReadOnlyList<AiGuidePage> Pages(string stamp, AiTree[] trees)
    {
        if (stamp != NovemberStock) return AiGuide.Pages;
        AiAnchor Resolve(AiAnchor anchor)
        {
            string key = FormattableString.Invariant($"{(int)anchor.Tree}:{anchor.Node}:{anchor.Term}:{anchor.Arg}");
            if (!November.Value.TryGetValue(key, out var expected)) throw new InvalidDataException(L.Ai.TreeChanged);
            var moved = anchor with { Node = expected.Node };
            if (moved.Slot(trees) is not { } slot) throw new InvalidDataException(L.Ai.TreeChanged);
            AiTree tree = trees[(int)slot.Tree];
            AiNode node = tree.Nodes[slot.Node];
            uint block = AiValues.BlockOf(tree, node) is { } parent ? unchecked((uint)parent.Params[0]) : 0;
            if (node.Class != expected.Class || block != expected.Block || node.Params[slot.Param] != expected.Original ||
                !AiCatalog.IsNumber(node, slot.Param)) throw new InvalidDataException(L.Ai.TreeChanged);
            return moved;
        }
        return AiGuide.Pages.Select(page => page with
        {
            Sections = page.Sections.Select(section => section with
            {
                Values = section.Values.Select(value => value with { Anchors = value.Anchors.Select(Resolve).ToArray() }).ToArray()
            }).ToArray()
        }).ToArray();
    }

    internal static IEnumerable<AiGuideValue> Values(IReadOnlyList<AiGuidePage> pages) =>
        pages.SelectMany(page => page.Sections).SelectMany(section => section.Values);
}
