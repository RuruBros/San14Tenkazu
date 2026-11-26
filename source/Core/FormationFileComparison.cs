using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace San14Tenkazu;

internal sealed record FormationFileRow(int Id, string NameBytes, byte Kind, Dictionary<string, byte> Values)
{
    internal string Name => Encoding.Unicode.GetString(Convert.FromHexString(NameBytes)).Split('\0')[0];
}
internal sealed record FormationFileData(string Sha256, string Sha1, long Length, bool Decoded, FormationFileRow[] Rows,
    Dictionary<string, FileRuleRow[]>? Tables = null)
{
    internal const string RelativePath = "0010_KO/fixdataexce.s14";
    private const int Limit = 32 * 1024 * 1024, Block = 754, Stride = 152;
    private static readonly (string Field, int Offset)[] Fields =
        [("mobility", 0x6f), ("attack", 0x73), ("siege", 0x74), ("break", 0x75), ("defense", 0x76)];

    internal static FormationFileData Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > Limit) throw new InvalidDataException(L.FileComparison.FormatError);
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        bool decoded = Path.GetExtension(path).Equals(".decoded", StringComparison.OrdinalIgnoreCase);
        byte[] body = bytes;
        if (!decoded)
        {
            if (!LwcCodec.IsBlock(bytes, Block) || BinaryPrimitives.ReadInt32LittleEndian(bytes) != 0x66
                || !bytes.AsSpan(4).StartsWith("SN14SCEXVER0001"u8)
                || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(Block + 4)) is <= 0 or > Limit)
                throw new InvalidDataException(L.FileComparison.FormatError);
            body = LwcCodec.Decode(bytes, Block);
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(463));
            uint sum = 0;
            foreach (byte value in body) sum += value;
            if (BigInteger.ModPow(signature, 0x220f799, 0x3fde03a3) != (sum & 0xffff))
                throw new InvalidDataException(L.FileComparison.FormatError);
        }
        var tables = FileRuleLayout.Read(body);
        int start = FileRuleLayout.Locate(body, 11, 21, Stride, 201);
        if (start < 0 && tables.Count == 0) throw new InvalidDataException(L.FileComparison.TableError);
        return new(Convert.ToHexString(SHA256.HashData(bytes)), Convert.ToHexString(SHA1.HashData(bytes)),
            bytes.Length, decoded, start < 0 ? [] : ReadRows(body, start), tables);
    }

    internal static FormationFileRow[] ReadRows(ReadOnlySpan<byte> body)
    {
        int first = FileRuleLayout.Locate(body, 11, 21, Stride, 201);
        if (first < 0) throw new InvalidDataException(L.FileComparison.TableError);
        return ReadRows(body, first);
    }
    private static FormationFileRow[] ReadRows(ReadOnlySpan<byte> body, int first)
    {
        var rows = new FormationFileRow[20];
        foreach (int id in FormationRules.EditableIds)
        {
            var row = body.Slice(first + id * Stride, Stride);
            var values = new Dictionary<string, byte>(Fields.Length);
            foreach (var (field, offset) in Fields) values.Add(field, row[offset]);
            rows[id - 1] = new(id, Convert.ToHexString(row[..10]), row[0x6e], values);
        }
        return rows;
    }
}

internal sealed record OfficialFormation(ulong Manifest, string Sha256, FormationFileRow[] Rows,
    Dictionary<string, FileRuleRow[]>? Tables = null)
{
    internal const uint Depot = 4508240;
    internal static readonly OfficialFormation[] Known = Load();
    private static OfficialFormation[] Load()
    {
        using var stream = typeof(OfficialFormation).Assembly.GetManifestResourceStream("BuildProfiles.OfficialFormationRows.json")!;
        var rows = JsonSerializer.Deserialize<FormationFileRow[]>(stream)!;
        // Both archived originals were independently decoded and have the same 20 rows.
        var tables = FileRuleLayout.ReadOfficial("BuildProfiles.PreviousFixedRules.json.gz");
        return [new(8895739041082902174, "D3FAC398D257407DCD81B0620BFD23F0956C3382C8926A5BD7EE4913DA105A25", rows, tables),
                new(3828791053776321157, "BBCF4F5023B9E870F0C51E6F37B0F95401ABE6E32EC29FDDD75FDBA52D4C07B7", rows, tables)];
    }
}

internal sealed record FormationFileField(int Id, string Field, byte Base, byte Target, byte File, bool IdentityChanged);

internal sealed record FormationFileComparison(FormationFileField[] Fields, ulong? OfficialFile)
{
    internal static FormationFileComparison Compare(OfficialFormation original, OfficialFormation target,
        FormationFileData file, IEnumerable<OfficialFormation> references)
    {
        var fields = new List<FormationFileField>();
        foreach (var row in file.Rows)
        {
            if (original.Rows.Length == 0 || target.Rows.Length == 0) break;
            var before = original.Rows[row.Id - 1];
            var after = target.Rows[row.Id - 1];
            bool changedIdentity = before.NameBytes != after.NameBytes || before.Kind != after.Kind
                || after.NameBytes != row.NameBytes || after.Kind != row.Kind;
            foreach (var field in FormationRules.Fields)
                fields.Add(new(row.Id, field.Id, before.Values[field.Id], after.Values[field.Id], row.Values[field.Id], changedIdentity));
        }
        ulong? match = file.Decoded ? null : references.Where(r => r.Sha256 == file.Sha256)
            .OrderByDescending(r => r.Manifest == target.Manifest).Select(r => (ulong?)r.Manifest).FirstOrDefault();
        return new(fields.ToArray(), match);
    }
}
