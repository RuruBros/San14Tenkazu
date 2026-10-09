using System.IO.Compression;
using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

// The vectors source/Reverse/vectors/*.py writes: a snapshot of the game's objects (world.json.gz) and, per formula,
// cases the game's own code answered in the emulator, some on objects changed first ("pokes").
internal static class Vectors
{
    internal static readonly string Dir = Path.Combine(AppContext.BaseDirectory, "Vectors");

    internal static JsonElement Read(string name)
    {
        using var file = File.OpenRead(Path.Combine(Dir, name));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        return JsonDocument.Parse(gz).RootElement.Clone();
    }

    internal static World LoadWorld(string name)
    {
        JsonElement root = Read(name);
        var tables = new Dictionary<int, Rec[]>();
        foreach (JsonProperty t in root.GetProperty("tables").EnumerateObject())
        {
            int raw = Convert.ToInt32(t.Name, 16);
            ulong address = t.Value.TryGetProperty("address", out JsonElement nativeAddress)
                ? Convert.ToUInt64(nativeAddress.GetString()![2..], 16) : 0;
            int size = t.Value.GetProperty("size").GetInt32();
            Rec[] recs = t.Value.GetProperty("records").EnumerateArray()
                .Select((r, i) => new Rec(i, Convert.FromHexString(r.GetString()!), raw, address == 0 ? 0 : address + (ulong)(i * size))).ToArray();
            foreach (JsonProperty e in t.Value.GetProperty("extra").EnumerateObject())
            {
                if (e.Value.TryGetProperty("vec40", out JsonElement v)) recs[int.Parse(e.Name)].Vector40 = Convert.FromHexString(v.GetString()!);
                if (e.Value.TryGetProperty("vec50", out JsonElement v50)) recs[int.Parse(e.Name)].Vector50 = Convert.FromHexString(v50.GetString()!);
                if (e.Value.TryGetProperty("vec98", out JsonElement v98)) recs[int.Parse(e.Name)].Vector98 = Convert.FromHexString(v98.GetString()!);
                if (e.Value.TryGetProperty("vecC8", out JsonElement vc8)) recs[int.Parse(e.Name)].VectorC8 = Convert.FromHexString(vc8.GetString()!);
                if (e.Value.TryGetProperty("vecA8", out JsonElement va8)) recs[int.Parse(e.Name)].VectorA8 = Convert.FromHexString(va8.GetString()!);
                if (e.Value.TryGetProperty("vecA0", out JsonElement va0)) recs[int.Parse(e.Name)].VectorA0 = Convert.FromHexString(va0.GetString()!);
                if (e.Value.TryGetProperty("vec70", out JsonElement v70)) recs[int.Parse(e.Name)].Vector70 = Convert.FromHexString(v70.GetString()!);
                if (e.Value.TryGetProperty("vec1D0", out JsonElement v1D0)) recs[int.Parse(e.Name)].Vector1D0 = Convert.FromHexString(v1D0.GetString()!);
            }
            tables[raw] = recs;
        }
        var lists = new Dictionary<int, List<Rec?[]>>();
        if (root.TryGetProperty("lists", out JsonElement ls))
            foreach (JsonProperty l in ls.EnumerateObject())
                lists[Convert.ToInt32(l.Name, 16)] = l.Value.GetProperty("items").EnumerateArray()
                    .Select(item => item.EnumerateArray().Select(r => Ref(tables, r)).ToArray()).ToList();
        int[] byId = root.TryGetProperty("byid", out JsonElement b) ? b.EnumerateArray().Select(x => x.GetInt32()).ToArray() : [];
        Rec state = new(0, root.TryGetProperty("worldstate", out JsonElement ws) ? Convert.FromHexString(ws.GetString()!) : new byte[0x1800]);
        var flags = new Dictionary<string, int>();
        if (root.TryGetProperty("flags", out JsonElement fl))
            foreach (JsonProperty f in fl.EnumerateObject()) flags[f.Name] = f.Value.GetInt32();
        var w = new World(tables, new Rec(0, Convert.FromHexString(root.GetProperty("settings").GetString()!)), lists, byId, state, flags);
        if (root.TryGetProperty("cmdactions", out JsonElement ca))
            w.CmdActions = ca.EnumerateArray().Select(a => (a.GetProperty("kind").GetInt32(),
                a.GetProperty("officers").EnumerateArray().Select(x => x.GetInt32()).ToList())).ToList();
        return w;
    }

    // [table, index] -> that record, null -> null.
    private static Rec? Ref(IReadOnlyDictionary<int, Rec[]> tables, JsonElement r) =>
        r.ValueKind == JsonValueKind.Null ? null : tables[Convert.ToInt32(r[0].GetString()!, 16)][r[1].GetInt32()];

    // Applies a case's pokes ([table, index, offset, hex], table "settings" for the options object,
    // ["list", head, index, ""] to append a record to a list, ["vec40", office, length, ""] to cut an office's
    // bonus vector) and returns what puts the world back.
    internal static Action Poke(World w, JsonElement pokes)
    {
        var undo = new List<(byte[] Bytes, int At, byte[] Old)>();
        var appended = new List<List<Rec?[]>>();
        var restore = new List<Action>();
        foreach (JsonElement p in pokes.EnumerateArray())
        {
            string table = p[0].GetString()!;
            if (table == "listdata")
            {
                List<Rec?[]> list = w.List(Convert.ToInt32(p[1].GetString()!, 16));
                Rec?[][] old = list.ToArray();
                list.Clear();
                list.AddRange(p[2].EnumerateArray().Select(e => e.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(r[0].GetString()!, 16))[r[1].GetInt32()]).ToArray()));
                restore.Add(() => { list.Clear(); list.AddRange(old); });
                continue;
            }
            if (table == "list")
            {
                List<Rec?[]> list = w.List(Convert.ToInt32(p[1].GetString()!, 16));
                list.Add(p[2].ValueKind == JsonValueKind.Number
                    ? [w.Table(Tables.Items)[p[2].GetInt32()]]
                    : p[2].EnumerateArray().Select(r => r.ValueKind == JsonValueKind.Null ? null : w.Table(Convert.ToInt32(r[0].GetString()!, 16))[r[1].GetInt32()]).ToArray());
                appended.Add(list);
                continue;
            }
            if (table == "exe")
            {
                // an exe global the formulas read as a value (World.Value)
                string name = p[1].GetString()!;
                int old = w.Value(name);
                w.SetValue(name, BitConverter.ToInt32(Convert.FromHexString(p[3].GetString()!)));
                restore.Add(() => w.SetValue(name, old));
                continue;
            }
            if (table is "vec" or "vecdata")
            {
                // a record's vector cut (or run on, over zeros) to a length: [table, index], [at, length]
                Rec r = w.Table(Convert.ToInt32(p[1][0].GetString()!, 16))[p[1][1].GetInt32()];
                int vat = table == "vecdata" ? p[2].GetInt32() : p[2][0].GetInt32();
                Func<byte[]?> get = vat switch { 0x40 => () => r.Vector40, 0x50 => () => r.Vector50, 0x70 => () => r.Vector70, 0x98 => () => r.Vector98, 0xA8 => () => r.VectorA8, 0xA0 => () => r.VectorA0, _ => () => r.VectorC8 };
                Action<byte[]?> set = vat switch
                {
                    0x40 => v => r.Vector40 = v, 0x50 => v => r.Vector50 = v, 0x70 => v => r.Vector70 = v, 0x98 => v => r.Vector98 = v, 0xA8 => v => r.VectorA8 = v, 0xA0 => v => r.VectorA0 = v, _ => v => r.VectorC8 = v,
                };
                byte[]? was = get();
                byte[] cut;
                if (table == "vecdata") cut = Convert.FromHexString(p[3].GetString()!);
                else
                {
                    int length = p[2][1].GetInt32();
                    cut = new byte[length];
                    (was ?? []).AsSpan(0, Math.Min(length, was?.Length ?? 0)).CopyTo(cut);
                }
                set(cut);
                restore.Add(() => set(was));
                continue;
            }
            if (table == "vec40")
            {
                // the office's bonus vector cut (or run on, over zeros) to a length
                Rec office = w.Table(Tables.Offices)[p[1].GetInt32()];
                byte[]? old = office.Vector40;
                byte[] cut = new byte[p[2].GetInt32()];
                (old ?? []).AsSpan(0, Math.Min(cut.Length, old?.Length ?? 0)).CopyTo(cut);
                office.Vector40 = cut;
                restore.Add(() => office.Vector40 = old);
                continue;
            }
            byte[] bytes = table switch
            {
                "settings" => w.Settings.Bytes,
                "worldstate" => w.WorldState.Bytes,
                _ => w.Table(Convert.ToInt32(table, 16))[p[1].GetInt32()].Bytes,
            };
            int at = p[2].GetInt32();
            byte[] data = Convert.FromHexString(p[3].GetString()!);
            undo.Add((bytes, at, bytes.AsSpan(at, data.Length).ToArray()));
            data.CopyTo(bytes, at);
        }
        return () =>
        {
            for (int k = undo.Count - 1; k >= 0; k--) undo[k].Old.CopyTo(undo[k].Bytes, undo[k].At);
            foreach (List<Rec?[]> list in appended) list.RemoveAt(list.Count - 1);
            for (int k = restore.Count - 1; k >= 0; k--) restore[k]();
        };
    }
}
