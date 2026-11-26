using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace San14Tenkazu;

internal sealed record RuleFileCell(int Offset, int Width, string Row, string Label, int Value);

internal sealed record RuleFileSnapshot(RuleProfile Profile, Dictionary<RuleKey, RuleFileCell> Cells)
{
    internal static RuleFileSnapshot Read(string build, FormationFileData data, byte[] body)
    {
        // Fixed data starts with version 102 and the region table. Scenario/save bodies have other headers.
        if (data.Decoded || body.Length < 8 || BinaryPrimitives.ReadInt32LittleEndian(body) != 102
            || BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(4)) != 12)
            throw new InvalidDataException(L.FileComparison.FormatError);
        int start = FileRuleLayout.Locate(body, 11, 21, 152, 201);
        if (data.Rows.Length != (start < 0 ? 0 : 20)) throw new InvalidDataException(L.FileComparison.TableError);
        int previousEnd = start < 0 ? 0 : start + 21 * 152;
        var catalogs = new List<RuleCatalog>();
        var cells = new Dictionary<RuleKey, RuleFileCell>();
        using var formations = new MemoryStream();
        foreach (var row in data.Rows)
        {
            formations.WriteByte((byte)row.Id);
            formations.Write(Convert.FromHexString(row.NameBytes));
            formations.WriteByte(row.Kind);
            foreach (var field in FormationRules.Fields)
                Add(FormationRules.Key(row.Id, field), start + row.Id * 152 + field.Plain - 0x10, 1,
                    row.Name, field.Label, row.Values[field.Id]);
        }
        byte[] formationBytes = formations.ToArray();
        string fingerprint = Convert.ToHexString(SHA256.HashData(formationBytes));
        if (data.Rows.Length > 0) catalogs.Add(new("formation", "formation-v1", fingerprint, FormationRules.EditableIds));

        foreach (var layout in FileRuleLayout.All)
        {
            start = FileRuleLayout.Locate(body, layout.Preceding, layout.Count, layout.Stride, layout.FollowingCount);
            if (start < 0) continue;
            if (start < previousEnd + 8 || data.Tables is null || !data.Tables.TryGetValue(layout.Table, out var rows))
                throw new InvalidDataException(L.FileComparison.TableError);
            previousEnd = start + layout.Count * layout.Stride;
            string[] fields = layout.Table == "tactic" ? TacticRules.Fields.Select(f => f.Id).ToArray()
                : PolicyRules.Fields(layout.Table).Select(f => f.Id).ToArray();
            using var identities = new MemoryStream();
            using var canonical = new MemoryStream();
            var ids = new List<int>();
            for (int id = 1; id < rows.Length; id++)
            {
                var row = rows[id];
                byte[] name = Convert.FromHexString(row.NameBytes);
                identities.WriteByte((byte)id);
                identities.Write(name);
                canonical.WriteByte((byte)id);
                canonical.Write(name, 0, Math.Min(12, name.Length));
                if (layout.Table == "tactic") { identities.WriteByte(row.Kind); canonical.WriteByte(row.Kind); }
                string shortName = GameMemory.ReadName(name, 0, layout.Table == "tactic" ? 5 : 6);
                if (GameMemory.IsPlaceholder(shortName) || layout.Table != "tactic" && shortName == "0") continue;
                ids.Add(id);
                for (int f = 0; f < fields.Length; f++)
                {
                    var column = layout.Columns[f];
                    if (column.Bit >= 0 || column.Width is not (1 or 2) || column.Offset + column.Width > layout.Stride)
                        throw new InvalidDataException(L.FileComparison.TableError);
                    Add(new(layout.Table, id, fields[f]), start + id * layout.Stride + column.Offset,
                        column.Width, row.Name, column.Label, int.Parse(row.Values[f], CultureInfo.InvariantCulture));
                }
            }
            if (ids.Count > 0)
                catalogs.Add(new(layout.Table, layout.Table + "-v1", Convert.ToHexString(SHA256.HashData(canonical.ToArray())),
                    ids.ToArray(), Convert.ToHexString(identities.ToArray())));
        }
        var profile = new RuleProfile(RuleProfile.CurrentVersion, build, RuleProfile.CurrentSchema, fingerprint, false,
            cells.Select(c => new RuleDefault(c.Key, c.Value.Value)).ToArray(), [], catalogs.ToArray(),
            data.Rows.Length == 0 ? null : Convert.ToHexString(formationBytes));
        profile.ValidateFileProjection();
        return new(profile, cells);

        void Add(RuleKey key, int offset, int width, string row, string label, int value)
        {
            int raw = width == 1 ? body[offset] : BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset));
            if (value != raw) throw new InvalidDataException(L.FileComparison.TableError);
            cells.Add(key, new(offset, width, row, label, value));
        }
    }
}
