using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// Prisoner release and return initiation compose the verified free-officer and return-command bodies.
internal sealed class PrisonerRelease(World w, OfficerLifecycle officers, CommandStorage commands,
    Func<Rec?> allocateReturn, Action<CommandCancellation.Command> refreshReturn)
{
    [Rva(0x1A499D8)] internal const int ExclusionDecay = 50;
    private static void Word(Rec p, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(p.Bytes.AsSpan(at), unchecked((ushort)value));

    private void RequireNonPlayer(Rec p)
    {
        if (Objects.IsPlayerForce(w, Objects.ForceOf(w, p)))
            throw new NotSupportedException("Player prisoner release requires its notification/UI host requests.");
        if (commands.FindOfficer(p.U16(0x10)) is { } command) CommandTravel.RequireDispatch(command);
    }

    private int Free(Rec p) => (p.U16(0x15E) & 0x4000) != 0 ? officers.ApplyMarked(p) : officers.ApplyFree(p);

    // 0x2EA6D0, arguments2/3 zero. An invalid/lost home releases the officer; arrival writes place only.
    internal int StartReturn(Rec p)
    {
        RequireNonPlayer(p);
        Rec force = Objects.ForceOf(w, p);
        Rec? home = Objects.ForceValid(force) ? Map.Base(w, p.U16(0xE2)) : null;
        if (!Objects.Valid(w, home) || Map.BaseForce(w, home!) != force.Index)
        {
            Free(p);
            return 0;
        }
        Rec location = CommandTravel.OfficerLocation(w, commands, p) ??
            throw new InvalidOperationException("The native return-start location is null.");
        if (location.Index == Map.BaseHex(w, home!))
        {
            Word(p, 0xE4, Math.Min(home!.U16(0x4E), (ushort)1061));
            return 0;
        }
        ReturnCommand.CreateWithHomeFallback(w, commands, new(null, [p], false), allocateReturn, refreshReturn);
        return 1;
    }

    // 0x2E75C0 with notification zero. Restore the original force's ruler city/main corps before returning.
    internal int Apply(Rec? person)
    {
        if (!Objects.Valid(w, person)) return 0;
        Rec p = person!;
        RequireNonPlayer(p);
        Rec force = Objects.ForceOf(w, p);
        Rec? main = Objects.ForceValid(force) ? Map.Base(w, w.OfficerById(force.U16(0x10))!.U16(0xE2)) : null;
        Rec? city = main is not null ? OfficerCapture.ParentCity(w, main) : null;
        if (!Objects.Valid(w, city))
        {
            Rec? excluded = w.OfficerById(p.U16(0x162));
            if (Objects.Valid(w, excluded) && p.I16(0x16C) < 10)
            {
                int value = 0;
                if (p.I16(0x16C) < -9) Word(p, 0x162, 0);
                else value = Math.Clamp(Math.Max(1, p.U8(0x164) * ExclusionDecay / 100), 0, 255);
                p.Bytes[0x164] = (byte)value;
            }
            Free(p);
        }
        else
        {
            Rec district = w.List(Lists.CorpsList).Select(n => n[0]).OfType<Rec>().FirstOrDefault(c =>
                Objects.CorpsValid(c) && c.U8(0x10) == force.Index &&
                (c.U16(0x12) == force.U16(0x10) || c.U8(0x11) == 1)) ?? w.Table(Tables.Corps)[0];
            Word(p, 0xE2, Math.Min(city!.U16(0x4E), (ushort)61));
            p.Bytes[0xE6] = 4;
            CorpsLifecycle.SetOfficerCorps(w, p, district.Index);
            p.Bytes[0x165] = 0;
            StartReturn(p);
        }
        return 1;
    }

    // 0x230B10: registered status6 officers whose encoded place matches this unit, in original list order.
    internal static IEnumerable<Rec> HeldBy(World w, Rec unit) => w.List(Lists.OfficerList).Select(n => n[0]).OfType<Rec>()
        .Where(p => p.U8(0xE6) == 6 && p.U16(0xE4) == ((uint)(unit.Index - 1) < 500 ? unit.Index + 61 : 0));
}
