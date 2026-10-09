using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// 0x293050 with logical native cache/list pools. Verified command storage and return allocation inputs
// enable active-command cancellation; player presentation and unavailable pools are separate branches.
internal sealed class CorpsLifecycle(World w, IDictionary<ulong, ulong> corpsCache,
    IReadOnlyDictionary<int, List<Rec?>> cautionMembers, Func<bool> nodePoolEnabled,
    CommandStorage? commandStorage = null, Func<Rec?>? allocateReturn = null,
    Action<CommandCancellation.Command>? refreshReturn = null)
{
    [Rva(0x1A4838C)] internal const int BudgetHigh = 200000;
    [Rva(0x1A48380)] internal const int BudgetMiddle = 150000;
    [Rva(0x1A48374)] internal const int BudgetLow = 100000;
    [Rva(0x1A4836C)] internal const int BudgetDefault = 50000;

    private static void Word(Rec r, int at, int v) =>
        BinaryPrimitives.WriteUInt16LittleEndian(r.Bytes.AsSpan(at), unchecked((ushort)v));
    private static void Dword(Rec r, int at, int v) =>
        BinaryPrimitives.WriteInt32LittleEndian(r.Bytes.AsSpan(at), v);
    private static bool Serving(Rec p) => p.U8(0xE6) is >= 1 and <= 4;

    private IEnumerable<Rec> Officers(Rec corps) => w.List(Lists.OfficerList).Select(n => n[0]).OfType<Rec>()
        .Where(p => corps.Index is >= 1 and <= 51 && p.U8(0xE0) == corps.Index);
    private IEnumerable<Rec> Cities(Rec corps) => w.Table(Tables.Cities)
        .Where(c => corps.Index is >= 1 and <= 51 && c.U8(0x30) == corps.Index);
    private IEnumerable<Rec> Gates(Rec city) => w.List(Lists.GateList).Select(n => n[0]).OfType<Rec>()
        .Where(g => OfficerCapture.ParentCity(w, g) == city);

    internal static Rec MainCorps(World w, Rec force) => w.List(Lists.CorpsList).Select(n => n[0]).OfType<Rec>()
        .FirstOrDefault(c => Objects.CorpsValid(c) && c.U8(0x10) == force.Index &&
            (c.U16(0x12) == force.U16(0x10) || c.U8(0x11) == 1)) ?? w.Table(Tables.Corps)[0];

    // Reject unsupported command/presentation bodies before the first gameplay write.
    private void RequireSupported(Rec source, Rec destination)
    {
        if (!nodePoolEnabled())
            throw new NotSupportedException("An unavailable native list pool is outside this branch.");
        Rec from = w.Entry(Tables.Forces, source.U8(0x10), 51);
        Rec to = w.Entry(Tables.Forces, destination.U8(0x10), 51);
        if (Objects.IsPlayerForce(w, from) || Objects.IsPlayerForce(w, to))
            throw new NotSupportedException("Player corps presentation has a separate engine body.");
        if (Officers(source).Any(p => Serving(p) && w.CmdActions.Any(c => c.Officers.Contains(p.U16(0x10)))))
        {
            if (commandStorage is null || allocateReturn is null || refreshReturn is null)
                throw new NotSupportedException("Active command cancellation requires its verified storage and return allocation input.");
            foreach (Rec person in Officers(source).Where(Serving))
                if (commandStorage.FindOfficer(person.U16(0x10)) is { } command)
                {
                    CommandCancellation.RequireSupported(w, command);
                    foreach (int id in command.Officers)
                        if (w.OfficerById(id) is { } member && Serving(member) && commandStorage.FindOfficer(id) is { } current)
                            CommandTravel.RequireDispatch(current);
                }
        }
    }

    internal void RequireMergeSupported(Rec source, Rec destination) => RequireSupported(source, destination);

    // 0x2311B0 -> 0x2311F0: the force's title chooses the corps budget limit.
    internal static int MaximumBudget(World w, Rec corps) => w.Entry(Tables.Forces, corps.U8(0x10), 51).U8(0x47) switch
    {
        1 or 2 or 3 or 10 or 11 or 12 => BudgetHigh,
        4 or 5 => BudgetMiddle,
        6 or 7 => BudgetLow,
        _ => BudgetDefault,
    };

    // 0x240CB0: compare the unclamped input first, clear envoy appointments, reset cross-force counters,
    // then assign the clamped corps number. Player refresh is excluded by RequireSupported.
    internal static void SetOfficerCorps(World w, Rec p, int id)
    {
        if (id != p.U8(0xE0))
        {
            Rec oldForce = Objects.ForceOf(w, p);
            Rec corps = w.Entry(Tables.Corps, id, 51);
            Rec nextForce = w.Entry(Tables.Forces, corps.U8(0x10), 51);
            OfficerCapture.ClearEnvoy(w, p, oldForce);
            if (oldForce.Index != nextForce.Index)
            {
                p.Bytes[0x1C6] = 0;
                Word(p, 0x1C7, 0);
            }
        }
        p.Bytes[0xE0] = (byte)Math.Clamp(id, 0, 51);
    }

    private void Governor(Rec b)
    {
        if (Objects.Valid(w, b)) OfficerCapture.AssignGovernor(w, b, OfficerCapture.SelectGovernor(w, b));
    }

    private void EmptyBase(Rec b)
    {
        Dword(b, 0x3C, Math.Min(Engagement.MaxTroops(w, b), 0));
        int force = Map.BaseForce(w, b);
        if ((uint)(force - 46) > 4) b.Bytes[0x44] = 0;
        Rec building = w.Entry(Tables.Buildings, b.U16(0x4E), 3000);
        if (Map.BuildingValid(building))
        {
            int maximum = Engagement.MaxDurability(w, b);
            int durability = maximum / 4;
            durability = durability < 0 ? 0 : Math.Min(durability, maximum);
            Word(building, 0x14, Math.Clamp(durability, 0, 9999));
        }
        Dword(b, 0x40, Math.Min(Engagement.MaxTroops(w, b), 0));
    }

    // 0x32AEC0 -> 0x226A00: clear every referencing troop, reset the caution and erase all registered aliases.
    private void RemoveCaution(Rec caution)
    {
        foreach (Rec troop in w.Table(Tables.Troops))
            if (troop.U16(0x14) == 1 && troop.U16(0x16) == caution.Index) Word(troop, 0x16, 0);
        byte[] b = caution.Bytes;
        b.AsSpan(0x10, 10).Clear();
        if (nodePoolEnabled() && cautionMembers.TryGetValue(caution.Index, out List<Rec?>? members)) members.Clear();
        b[0x38] = 0;
        b.AsSpan(0x1A, 4).Clear();
        b.AsSpan(0x20, 4).Clear();
        b.AsSpan(0x3C, 12).Clear();
        w.List(Lists.CautionTroopList).RemoveAll(n => ReferenceEquals(n[0], caution));
    }

    // 0x2EDF20: retain bit 31 while enabling bits 0..30; preserve the record's other fields.
    internal static void ResetOrders(Rec corps)
    {
        corps.Bytes[0x17] = 1;
        Word(corps, 0x19, 0xFF);
        corps.Bytes[0x1B] = 0xFF;
        Dword(corps, 0x1C, corps.I32(0x1C) | 0x7FFFFFFF);
        Word(corps, 0x20, 0);
        corps.Bytes[0x22] = 0;
        corps.Bytes[0x18] = 0;
    }

    // 0x226C10: preserve the header, byte +0x23 and trailing padding.
    internal static void Reset(Rec corps)
    {
        Dword(corps, 0x10, 0);
        Dword(corps, 0x14, 0x01000000);
        Dword(corps, 0x18, unchecked((int)0xFF000000));
        Dword(corps, 0x1C, 0x7FFFFFFF);
        Word(corps, 0x20, 0);
        corps.Bytes[0x22] = 0;
        corps.Bytes[0x2A] = 0;
        Word(corps, 0x28, 0xFFFF);
        Dword(corps, 0x24, 0);
    }

    // 0x2E8E70 after the native force-survival guards. Force teardown is a separate engine body.
    internal int CheckSurvivingForce(Rec? source, int allowStructural = 0)
    {
        if (source is null || !Objects.CorpsValid(source) && (allowStructural == 0 || source.Index == 0 ||
            source.U8(0x10) == 0 || source.U8(0x11) == 0)) return 0;
        Rec force = w.Entry(Tables.Forces, source.U8(0x10), 51);
        bool forceServing = w.List(Lists.OfficerList).Select(n => n[0]).OfType<Rec>()
            .Any(p => Serving(p) && Objects.ForceOf(w, p) == force && OfficerCapture.CountedOfficer(w, p));
        if (Capture.CityCount(w, force) == 0 || !forceServing)
            throw new NotSupportedException("The force teardown body must run before corps reevaluation can continue.");
        if (!Cities(source).Any() || !Officers(source).Any(p => Serving(p) && OfficerCapture.CountedOfficer(w, p)))
        {
            Rec leader = w.OfficerById(source.U16(0x12))!;
            if (Objects.PersonValid(leader) && leader.U8(0xE6) == 1)
            {
                Rec? origin = Map.Base(w, leader.U16(0xE2));
                Rec? best = null;
                int length = int.MaxValue;
                foreach (Rec candidate in w.List(Lists.CorpsList).Select(n => n[0]).OfType<Rec>()
                    .Where(c => c.U8(0x10) == force.Index && !ReferenceEquals(c, source)))
                {
                    Rec? home = Map.Base(w, w.OfficerById(candidate.U16(0x12))!.U16(0xE2));
                    var route = origin is not null && home is not null ? CorpsPath.Find(w, origin, home) : new ReturnPath.Result([], 0);
                    int count = route.Success ? route.Hexes.Count : Map.Size * Map.Size;
                    if (best is null || count < length || count == length && candidate.U8(0x11) < best.U8(0x11))
                    {
                        best = candidate;
                        length = count;
                    }
                }
                if (best is null) throw new InvalidOperationException("Native main-corps replacement reads a null candidate head.");
                Merge(source, best, 0);
                best.Bytes[0x11] = 1;
            }
            else
            {
                Merge(source, MainCorps(w, force), 0);
            }
        }
        else OfficerCapture.ReevaluateRetainedCorps(w, source);
        return 1;
    }

    internal int Merge(Rec source, Rec? destination, int mode)
    {
        Rec dest = destination ?? w.Table(Tables.Corps)[0];
        RequireSupported(source, dest);
        Rec leader = w.OfficerById(source.U16(0x12))!;
        if (Serving(leader) && leader.U8(0xE6) != 1 && (mode == 0 || leader.U8(0xE6) != 3)) leader.Bytes[0xE6] = 4;
        Rec? leaderBase = Map.Base(w, leader.U16(0xE2));
        if (mode == 0 && Objects.Valid(w, leaderBase)) Governor(leaderBase!);
        foreach (Rec p in Officers(source))
        {
            if (Serving(p))
            {
                SetOfficerCorps(w, p, dest.Index);
                if (commandStorage?.FindOfficer(p.U16(0x10)) is { } command)
                    ReturnCommand.Cancel(w, commandStorage, command, allocateReturn!, refreshReturn!);
            }
            if (p.U8(0xE6) == 6) SetOfficerCorps(w, p, dest.Index);
        }
        foreach (Rec city in Cities(source))
        {
            city.Bytes[0x30] = (byte)Math.Clamp(dest.Index, 0, 51);
            if (!Objects.CorpsValid(dest))
            {
                EmptyBase(city);
                foreach (Rec gate in Gates(city)) EmptyBase(gate);
                city.Bytes[0x66] = (byte)Engagement.DamageLevel(w, city);
            }
            Governor(city);
            foreach (Rec gate in Gates(city)) Governor(gate);
        }
        if (Objects.CorpsValid(source) && Objects.CorpsValid(dest) && source.I32(0x24) > 0)
        {
            int amount = source.I32(0x24);
            Dword(source, 0x24, Math.Min(MaximumBudget(w, source), 0));
            int value = Math.Min(unchecked(amount + dest.I32(0x24)), MaximumBudget(w, dest));
            Dword(dest, 0x24, value < 0 ? 0 : Math.Min(value, MaximumBudget(w, dest)));
        }
        foreach (Rec caution in w.Table(Tables.CautionTroops))
            if (caution.Index != 0 && caution.U16(0x18) != 0 && caution.I32(0x14) == source.Index) RemoveCaution(caution);
        corpsCache.Remove(source.NativeAddress);
        foreach (Rec c in w.List(Lists.CorpsList).Select(n => n[0]).OfType<Rec>())
            if (c.U8(0x19) == 8 && c.U8(0x18) == source.Index) ResetOrders(c);
        w.List(Lists.CorpsList).RemoveAll(n => ReferenceEquals(n[0], source));
        Reset(source);
        return 0;
    }
}
