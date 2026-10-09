using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

internal static class CommandTravel
{
    [Rva(0x1A499F0)] internal const int StepTime = 35;
    [Rva(0x1A48BCC)] internal const int AdvantageDivisor = 2;
    internal sealed record Dispatch(int LocationGetter, int StartGetter, int TotalGetter, int ElapsedGetter, int TargetGetter);
    internal sealed record Destination(Rec Primary, int PrimaryKind, Rec Secondary, int SecondaryKind);
    internal static readonly Dispatch ReturnDispatch = new(0x22B1C0, 0x2312F0, 0x231940, 0x22E120, 0x231460);

    // 0x32BA50 reads signed month/day; 0x33A5B0 narrows the quotient to signed year/month before remainders.
    internal static int DateDays(Rec r, int at) => unchecked(r.I8(at + 3) - 31 + (r.I8(at + 2) + r.I16(at) * 12) * 30);
    internal static void SetDateDays(Rec r, int at, int days)
    {
        short year = unchecked((short)(days / 360));
        int remainder = Math.Max(0, unchecked(days - year * 360));
        sbyte month = unchecked((sbyte)(remainder / 30));
        BinaryPrimitives.WriteInt16LittleEndian(r.Bytes.AsSpan(at), year);
        r.Bytes[at + 2] = unchecked((byte)(month + 1));
        r.Bytes[at + 3] = unchecked((byte)(Math.Max(0, unchecked(remainder - month * 30)) + 1));
        r.Bytes[at + 4] = 0;
        // Byte +5 belongs to the host's original record and is preserved by both date setters.
    }

    // 0x333480: the default 0/1/1/0 date is invalid; the other fields use unsigned upper limits.
    internal static bool DateValid(Rec r, int at) =>
        (r.U16(at) != 0 || r.U8(at + 2) != 1 || r.U8(at + 3) != 1 || r.U8(at + 4) != 0) &&
        r.U16(at) < 352 && (uint)(r.U8(at + 2) - 1) < 13 && (uint)(r.U8(at + 3) - 1) < 31 && r.U8(at + 4) < 25;

    // 0x231790, exact return/location contract with a null person. The personality branch is not used here.
    internal static int Days(World w, int steps, Rec? force)
    {
        float advantage = force is not null && Objects.ForceValid(force) ? (float)Advantage.Effect(w, force, 5, 0) / AdvantageDivisor : 0f;
        float days = ((float)StepTime * steps / (advantage + 10f)) / 12f;
        return Math.Max(1, Objects.Trunc(MathF.Ceiling(days)));
    }

    internal static void RequireDispatch(CommandCancellation.Command command)
    {
        Dispatch? d = command.Travel;
        if (d is null || d.StartGetter != 0x2312F0 ||
            !(d.LocationGetter == 0x22B460 && d.TotalGetter == 0x231990 && d.ElapsedGetter == 0x22E170 ||
              d == ReturnDispatch && command.Destinations is { Count: > 0 }))
            throw new NotSupportedException("The command location dispatch and destination input have not been verified.");
    }

    internal static Rec Location(World w, CommandCancellation.Command command)
    {
        RequireDispatch(command);
        Rec data = command.Data;
        Rec source = w.Entry(Tables.Tiles, data.U16(0x4C), 48399);
        Rec target;
        if (command.Travel == ReturnDispatch)
        {
            Destination d = command.Destinations![0];
            if (d.PrimaryKind is not (3 or 4)) throw new NotSupportedException("This destination descriptor has not been ported.");
            target = Map.BuildingTile(w, d.Primary);
        }
        else target = w.Entry(Tables.Tiles, data.U16(0x6C), 48399);
        if (source.Index == target.Index) return source;
        ReturnPath.Result route = ReturnPath.Find(w, source.Index, target.Index);
        if (command.Travel == ReturnDispatch && !DateValid(data, 0x66))
        {
            Rec? first = command.Officers.Count != 0 ? w.OfficerById(command.Officers[0]) : null;
            Rec? force = first is not null && Objects.PersonValid(first) ? Objects.ForceOf(w, first) : null;
            SetDateDays(data, 0x66, unchecked(DateDays(w.WorldState, 0x34) + Days(w, Math.Max(1, route.Hexes.Count - 1), force)));
        }
        int total = unchecked(DateDays(data, 0x66) - DateDays(data, 0x60));
        if (total <= 0 || !route.Success) return target;
        int elapsed = unchecked(DateDays(w.WorldState, 0x34) - DateDays(data, 0x60));
        if (command.Travel == ReturnDispatch) elapsed = Math.Max(0, elapsed);
        int offset = unchecked(elapsed * route.Hexes.Count) / total;
        return offset < route.Hexes.Count ? w.Table(Tables.Tiles)[route.Hexes[Math.Max(0, offset)]] : target;
    }

    // 0x22D580: an active command takes precedence over the officer's encoded place.
    internal static Rec? OfficerLocation(World w, CommandStorage storage, Rec person)
    {
        var command = storage.FindOfficer(person.U16(0x10));
        if (command is not null) return Location(w, command);
        var (kind, number) = Objects.Place(person);
        return kind switch
        {
            5 => Map.BuildingTile(w, w.Entry(Tables.Cities, number, 51)),
            6 => Map.BuildingTile(w, w.Entry(Tables.Gates, number, 10)),
            10 => w.Entry(Tables.Tiles, w.Entry(Tables.Counties, number, 500).U16(0x3A), 48399),
            27 when Objects.UnitValid(w.Entry(Tables.Units, number, 500)) => w.Entry(Tables.Tiles, w.Entry(Tables.Units, number, 500).U16(0x2A), 48399),
            _ => null,
        };
    }

    // 0x22BBE0/0x22BBF0: gate corps belongs to its county's parent city and must share the gate's force.
    internal static int BaseCorps(World w, Rec home)
    {
        if (home.Table == Tables.Cities) return home.U8(0x30);
        Rec force = w.Entry(Tables.Forces, Map.BaseForce(w, home), 51);
        if (!Objects.ForceValid(force)) return 0;
        Rec county = w.Entry(Tables.Counties, Map.BuildingTile(w, home).U16(0x12), 500);
        Rec city = w.Entry(Tables.Cities, Objects.CountyValid(county) ? county.U8(0x35) : 0, 51);
        Rec corps = w.Entry(Tables.Corps, city.U8(0x30), 51);
        return corps.U8(0x10) == force.Index ? corps.Index : 0;
    }
}
