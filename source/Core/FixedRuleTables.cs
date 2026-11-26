using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace San14Tenkazu;

internal sealed record FileRuleRow(string NameBytes, byte Kind, string[] Values)
{
    internal string Name => Encoding.Unicode.GetString(Convert.FromHexString(NameBytes)).Split('\0')[0];
    internal bool SameIdentity(FileRuleRow other) => NameBytes == other.NameBytes && Kind == other.Kind;
}

internal sealed record FileRuleColumn(string Label, int Offset, int Width = 1, int Bit = -1)
{
    internal string Read(ReadOnlySpan<byte> row)
    {
        var bytes = row.Slice(Offset, Width);
        if (Bit >= 0) return ((BinaryPrimitives.ReadUInt32LittleEndian(bytes) >> Bit) & 1) == 0 ? "0" : "1";
        return Width switch
        {
            1 => bytes[0].ToString(System.Globalization.CultureInfo.InvariantCulture),
            2 => BinaryPrimitives.ReadUInt16LittleEndian(bytes).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert.ToHexString(SHA256.HashData(bytes))
        };
    }
}

internal sealed record FileRuleLayout(string Table, string Label, int Preceding, int Count, int Stride,
    int FollowingCount, int NameSize, FileRuleColumn[] Columns)
{
    internal static readonly FileRuleLayout[] All =
    [
        new("tactic", L.Rules.TacticPage, 12, 201, 94, 202, 10,
            [.. TacticRules.Fields.Select(f => new FileRuleColumn(f.Slot < 0 ? f.Label : $"{f.Label} {f.Slot + 1}", f.Plain - 0x12)),
             .. Enumerable.Range(0, 32).Select(i => new FileRuleColumn(L.FileComparison.Permission(i), 0x59, 4, i))]),
        new("policy", L.Rules.PolicyPage, 18, 121, 148, 31, 12,
            [.. PolicyRules.Effects.Select(f => new FileRuleColumn(f.Label, 0x88 + f.Slot)),
             new(L.FileComparison.PolicyFlag, 0x90), new(L.FileComparison.DescriptionDigest, 0x2c, 92)]),
        new("policyeffect", L.Rules.PolicyEffects, 26, 101, 134, 501, 22,
            PolicyRules.Levels.Select(f => new FileRuleColumn(f.Label, 0x72 + 2 * f.Slot, 2)).ToArray())
    ];

    internal static int Locate(ReadOnlySpan<byte> body, int previous, int count, int stride, int nextCount)
    {
        Span<byte> header = stackalloc byte[8], next = stackalloc byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, previous);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], count);
        BinaryPrimitives.WriteInt32LittleEndian(next, previous + 1);
        BinaryPrimitives.WriteInt32LittleEndian(next[4..], nextCount);
        int position = 0, found = -1;
        while (position < body.Length)
        {
            int index = body[position..].IndexOf(header);
            if (index < 0) break;
            int start = position + index + header.Length;
            if (body.Length - start >= count * stride + next.Length && body[(start + count * stride)..].StartsWith(next))
            {
                if (found >= 0) return -1;
                found = start;
            }
            position += index + 1;
        }
        return found;
    }

    internal static Dictionary<string, FileRuleRow[]> Read(ReadOnlySpan<byte> body)
    {
        var tables = new Dictionary<string, FileRuleRow[]>();
        foreach (var layout in All)
        {
            int start = Locate(body, layout.Preceding, layout.Count, layout.Stride, layout.FollowingCount);
            if (start < 0) continue;
            var rows = new FileRuleRow[layout.Count];
            for (int id = 0; id < rows.Length; id++)
            {
                var row = body.Slice(start + id * layout.Stride, layout.Stride);
                var values = new string[layout.Columns.Length];
                for (int c = 0; c < values.Length; c++) values[c] = layout.Columns[c].Read(row);
                rows[id] = new(Convert.ToHexString(row[..layout.NameSize]), layout.Table == "tactic" ? row[0x30] : (byte)0, values);
            }
            tables.Add(layout.Table, rows);
        }
        return tables;
    }

    internal static Dictionary<string, FileRuleRow[]> ReadOfficial(string resource)
    {
        using var source = typeof(FileRuleLayout).Assembly.GetManifestResourceStream(resource)!;
        using var stream = new GZipStream(source, CompressionMode.Decompress);
        return JsonSerializer.Deserialize<Dictionary<string, FileRuleRow[]>>(stream)!;
    }
}

internal sealed record FileRuleDifference(string Name, string Field, string Base, string Target, string File, bool IdentityChanged)
{
    internal bool Changed => Base != Target || Target != File || IdentityChanged;
    public string Result => IdentityChanged ? L.FileComparison.Identity : Base == Target ? (File == Target ? L.FileComparison.Same : L.FileComparison.Changed)
        : File == Target ? L.FileComparison.MatchesUpdate : File == Base ? L.FileComparison.OnlyUpdate : L.FileComparison.Overlap;

    internal static FileRuleDifference[]? Compare(FileRuleLayout layout, OfficialFormation start, OfficialFormation target, FormationFileData input)
    {
        if (start.Tables is null || !start.Tables.TryGetValue(layout.Table, out var old) ||
            target.Tables is null || !target.Tables.TryGetValue(layout.Table, out var current) ||
            input.Tables is null || !input.Tables.TryGetValue(layout.Table, out var rows)) return null;
        var fields = new List<FileRuleDifference>();
        for (int id = 0; id < rows.Length; id++)
        {
            var a = old[id]; var b = current[id]; var c = rows[id];
            string name = $"#{id} {c.Name}";
            fields.Add(new(name, L.FileComparison.IdentityField, Identity(a), Identity(b), Identity(c),
                !a.SameIdentity(b) || !b.SameIdentity(c)));
            for (int n = 0; n < layout.Columns.Length; n++)
                fields.Add(new(name, layout.Columns[n].Label, a.Values[n], b.Values[n], c.Values[n], false));
        }
        return fields.ToArray();

        static string Identity(FileRuleRow row) => $"{row.Name} [{row.NameBytes}, {row.Kind}]";
    }
}
