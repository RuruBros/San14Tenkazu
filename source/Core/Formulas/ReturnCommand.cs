using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal static class ReturnCommand
{
    private static void Word(Rec r, int at, int value) => BinaryPrimitives.WriteUInt16LittleEndian(r.Bytes.AsSpan(at), unchecked((ushort)value));
    private static void Dword(Rec r, int at, int value) => BinaryPrimitives.WriteInt32LittleEndian(r.Bytes.AsSpan(at), value);

    // 0x31AC30 -> 0x220A70 -> 0x220B70 -> 0x1D7DA0. Vtables and physical list wrappers are host objects.
    private static CommandCancellation.Command Initialize(Rec data)
    {
        Dword(data, 8, 0);
        data.Bytes[0x28] = 0;
        Word(data, 0x2A, 0);
        data.Bytes.AsSpan(0x40, 12).Clear();
        Word(data, 0x4C, 48400);
        data.Bytes.AsSpan(0x50, 8).Clear();
        Dword(data, 0x58, -1);
        Dword(data, 0x5C, 0);
        foreach (int at in new[] { 0x60, 0x66 })
        {
            data.Bytes.AsSpan(at, 6).Clear();
            data.Bytes[at + 2] = data.Bytes[at + 3] = 1;
        }
        data.Bytes.AsSpan(0x70, 12).Clear();
        return new(15, data, new List<int>(), 0x14D350, 0x1F1F70, 0x235620, 0x2289D0,
            CommandTravel.ReturnDispatch, new List<CommandTravel.Destination>());
    }

    internal static CommandCancellation.Command? Create(World w, CommandStorage storage,
        CommandCancellation.ReturnRequest request, Func<Rec?> allocate, Action<CommandCancellation.Command> refreshPlayer)
        => Create(w, storage, request, allocate, refreshPlayer, false);

    // 0x1F2360 has a guarded allocation/registration failure, unlike 0x31AC30's null virtual fallthrough.
    internal static CommandCancellation.Command? CreateWithHomeFallback(World w, CommandStorage storage,
        CommandCancellation.ReturnRequest request, Func<Rec?> allocate, Action<CommandCancellation.Command> refreshPlayer)
        => Create(w, storage, request, allocate, refreshPlayer, true);

    private static CommandCancellation.Command? Create(World w, CommandStorage storage,
        CommandCancellation.ReturnRequest request, Func<Rec?> allocate, Action<CommandCancellation.Command> refreshPlayer,
        bool homeFallback)
    {
        if (request.Officers.Count == 0) return null;
        foreach (Rec person in request.Officers)
            if (storage.FindOfficer(person.U16(0x10)) is { } current) CommandTravel.RequireDispatch(current);
        Rec? raw = allocate();
        var command = raw is null ? null : Initialize(raw);
        if (command is null || !storage.Register(command))
        {
            foreach (Rec person in request.Officers)
                Word(person, 0xE4, Math.Min(1061, (int)Map.Base(w, person.U16(0xE2))!.U16(0x4E)));
            if (homeFallback) return null;
            // 0x224670 dereferences the null command after these writes; preserve the failed engine step.
            throw new InvalidOperationException("Native return allocation failure reaches a null virtual call after place writes.");
        }
        Rec data = command.Data, first = request.Officers[0], home = Map.Base(w, first.U16(0xE2))!;
        Word(data, 0x2A, first.U16(0xE2));
        data.Bytes[0x28] = unchecked((byte)CommandTravel.BaseCorps(w, home));
        Rec initial = CommandTravel.OfficerLocation(w, storage, first) ?? throw new InvalidOperationException("Native return start location is null.");
        Word(data, 0x4C, initial.Index);
        foreach (Rec person in request.Officers)
        {
            Word(person, 0x15E, person.U16(0x15E) | 1);
            Rec location = CommandTravel.OfficerLocation(w, storage, person) ?? throw new InvalidOperationException("Native return officer location is null.");
            int county = location.U16(0x12);
            Word(person, 0xE4, county is >= 1 and <= 500 ? county + 561 : 0);
            ((List<int>)command.Officers).Add(person.U16(0x10));
            storage.Refresh();
        }
        int kind = home.U16(0x4E) < 52 ? 3 : 4;
        // 0x234EF0 copies five date bytes only when appending the first destination descriptor.
        w.WorldState.Bytes.AsSpan(0x34, 5).CopyTo(data.Bytes.AsSpan(0x60));
        ((List<CommandTravel.Destination>)command.Destinations!).Add(new(home, kind, home, kind));
        var route = ReturnPath.Find(w, initial.Index, Map.BuildingTile(w, home).Index);
        int days = CommandTravel.Days(w, Math.Max(1, route.Hexes.Count - 1), Objects.ForceOf(w, first));
        CommandTravel.SetDateDays(data, 0x66, unchecked(CommandTravel.DateDays(w.WorldState, 0x34) + days));
        Dword(data, 0x40, 0);
        Dword(data, 0x50, request.Notify ? 1 : 0);
        Rec force = w.Entry(Tables.Forces, w.WorldState.U8(0x3A), 51);
        Rec main = w.List(Lists.CorpsList).Select(n => n[0]).OfType<Rec>().FirstOrDefault(c =>
            Objects.CorpsValid(c) && c.U8(0x10) == force.Index &&
            (c.U16(0x12) == (force.Index != 0 ? force.U16(0x10) : 5014) || c.U8(0x11) == 1)) ?? w.Table(Tables.Corps)[0];
        if (Objects.CorpsValid(main) && data.U8(0x28) == main.Index) refreshPlayer(command);
        return command;
    }

    // 0x334780: refunds and return construction precede source removal, including callback order.
    internal static CommandCancellation.Command? Cancel(World w, CommandStorage storage, CommandCancellation.Command source,
        Func<Rec?> allocate, Action<CommandCancellation.Command> refreshPlayer)
    {
        var command = Create(w, storage, CommandCancellation.RefundAndRequestReturn(w, source), allocate, refreshPlayer);
        if (CommandCancellation.RequestRemoval(source) is { } removal) storage.Apply(removal);
        return command;
    }
}
