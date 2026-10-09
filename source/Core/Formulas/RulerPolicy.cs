using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// Policy reset bodies invoked after ruler replacement. The ID lookup at world+0xBCE0 is separate
// from the contiguous doctrine table at world+0x85F20; callers supply its observed pointer mapping.
internal sealed class RulerPolicy(World w, IReadOnlyList<Rec> doctrineById, Action? notifyPlayer = null)
{
    [Rva(0x1A48050)] internal const int ResetDays = 10;
    [Rva(0x1A48644)] internal const int MatchingClanDiscount = 1;

    internal void RequireInputs()
    {
        if (doctrineById.Count != 80) throw new InvalidDataException("Missing native doctrine ID lookup.");
    }

    private static void Word(Rec record, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(record.Bytes.AsSpan(at), unchecked((ushort)value));

    // 0x322D50: retain the original costs in bytes 2/5, discount only bytes 1/4, and reject a zero cost
    // for a nonzero resource kind. Zero-kind entries are allowed regardless of their cost byte.
    internal static byte[]? DoctrineCost(World w, int panel, Rec? person)
    {
        Rec entry = w.Entry(Tables.Doctrine, panel, 79);
        if (entry.Index == 0 || !Objects.Valid(w, person)) return null;
        byte[] cost = entry.Bytes.AsSpan(0x3D, 6).ToArray();
        if (cost[0] != 0)
        {
            if (cost[1] == 0) return null;
            cost[2] = cost[1];
            if (person!.U8(0x14C) == cost[0]) cost[1] = (byte)Math.Max(cost[1] - MatchingClanDiscount, 1);
        }
        if (cost[3] != 0)
        {
            if (cost[4] == 0) return null;
            cost[5] = cost[4];
            if (person!.U8(0x14C) == cost[3]) cost[4] = (byte)Math.Max(cost[4] - MatchingClanDiscount, 1);
        }
        return cost;
    }

    // 0x2E8D50: advisor reset precedes the optional morale cap, policy tier and durability refresh.
    internal static void ClearAdvisor(World w, Rec? force, bool refreshMorale)
    {
        if (!Objects.Valid(w, force)) return;
        Word(force!, 0x52, 0);
        Word(force!, 0x16C, 0);
        if (refreshMorale) OfficerCapture.RefreshMorale(w, force!);
        OfficerCapture.RefreshPolicyTier(w, force!);
        OfficerCapture.RefreshDurability(w, force!);
    }

    // 0x2E8C60: the appointment setter uses an unsigned field bound, while the delay store uses a
    // signed bound. A negative field may therefore clear preceding delay words without clearing IDs.
    internal static void ClearField(World w, Rec? force, int field)
    {
        if (Objects.Valid(w, force) && field < -3)
            throw new NotSupportedException("The native signed field would write before the force record.");
        for (int slot = 0; slot < 7; slot++)
        {
            if (!Objects.Valid(w, force)) continue;
            if ((uint)field < 5) Word(force!, 0x54 + (field * 15 + slot) * 2, 0);
            if (field < 5) Word(force!, 0x62 + (field * 15 + slot) * 2, 0);
        }
        if (!Objects.Valid(w, force)) return;
        if (field == 1) OfficerCapture.RefreshMorale(w, force!);
        else if (field == 4) OfficerCapture.RefreshPolicyTier(w, force!);
        else if (field == 3) OfficerCapture.RefreshDurability(w, force!);
    }

    private static void Resource(Rec force, int kind, int delta) =>
        force.Bytes[0x1C5 + kind - 1] = (byte)Math.Clamp(force.U8(0x1C5 + kind - 1) + delta, 0, 99);

    // 0x22C2C0 -> 0x3FD20: ranks above six repeatedly contribute rule zero, rather than being clamped.
    internal static int ResourcePoints(World w, Rec person)
    {
        if (person.U8(0xE6) == 1 || Objects.SpecialOfficer(w, person, false) ||
            (person.U16(0x15E) & 0x4000) != 0) return 0;
        int points = 0;
        for (int rank = person.U8(0x1BC); rank > 0; rank--)
            points += w.Entry(OfficerLifecycle.LoyaltyRules, rank, 6).U8(0x2E);
        return points;
    }

    // 0x232070: rebuild six resources from the registered serving roster, excluding the ruler.
    internal static void RefreshResources(World w, Rec force)
    {
        force.Bytes.AsSpan(0x1C5, 6).Clear();
        if (force.Index is < 1 or > 51) return;
        foreach (Rec?[] node in w.List(Lists.OfficerList))
        {
            Rec? person = node[0];
            if (person is null || person.U8(0xE6) is < 2 or > 4) continue;
            Rec corps = w.Entry(Tables.Corps, person.U8(0xE0), 51);
            if (!Objects.CorpsValid(corps) || corps.U8(0x10) != force.Index) continue;
            if ((person.U16(0x15E) & 0x4000) != 0 || Objects.SpecialOfficer(w, person, false)) continue;
            int clan = person.U8(0x14C);
            if (clan is >= 1 and <= 6) Resource(force, clan, ResourcePoints(w, person));
        }
    }

    // 0x235570 -> 0x2ED340(main corps, 1, 1): clear all active/disabled/available panels, then open
    // panel 1 when its native object/corps checks pass. Panel 1 bypasses the ordinary purchase gates.
    internal void ResetDoctrine(Rec force)
    {
        RequireInputs();
        force.Bytes.AsSpan(0x1CB, 30).Clear();
        force.Bytes[0x1DF] |= 1;
        Rec corps = CorpsLifecycle.MainCorps(w, force);
        if (Objects.CorpsValid(corps))
        {
            Rec owner = w.Entry(Tables.Forces, corps.U8(0x10), 51);
            if (Objects.ForceValid(owner) && ReferenceEquals(corps, CorpsLifecycle.MainCorps(w, owner)))
            {
                byte[]? cost = DoctrineCost(w, 1, w.OfficerById(owner.U16(0x10)));
                if (cost is not null)
                {
                    if (cost[0] != 0) Resource(owner, cost[0], -cost[1]);
                    if (cost[3] != 0) Resource(owner, cost[3], -cost[4]);
                    owner.Bytes[0x1CB] |= 1;
                    owner.Bytes[0x1D5] &= 0xFE;
                    Rec panel = w.Table(Tables.Doctrine)[1];
                    for (int i = 0; i < 6; i++)
                    {
                        int id = panel.U8(0x48 + i);
                        Rec linked = id < 80 ? doctrineById[id] : w.Table(Tables.Doctrine)[0];
                        if (linked.Index != 0) owner.Bytes[0x1DF + (linked.Index - 1) / 8] |= (byte)(1 << ((linked.Index - 1) % 8));
                    }
                }
            }
        }
        ClampGateTroops(force);
        ClampFacilityDurability(force);
    }

    // 0x335120: refund every enabled panel using the supplied old ruler, then reset the panel tree.
    internal void RefundDoctrine(Rec force, Rec? person)
    {
        RequireInputs();
        person ??= w.OfficerById(force.U16(0x10));
        for (int panel = 1; panel < 80; panel++)
        {
            if ((force.U8(0x1CB + (panel - 1) / 8) & (1 << ((panel - 1) % 8))) == 0) continue;
            byte[]? cost = DoctrineCost(w, panel, person);
            if (cost is null) continue;
            if (cost[0] != 0) Resource(force, cost[0], cost[1]);
            if (cost[3] != 0) Resource(force, cost[3], cost[4]);
        }
        ResetDoctrine(force);
    }

    // 0x224E30: a formerly available panel 56 can no longer raise gate troop capacity after reset.
    internal void ClampGateTroops(Rec force)
    {
        if (force.Index is < 1 or > 51 || (force.U8(0x1F5) & 0x80) == 0 || (force.U8(0x1D1) & 0x80) != 0) return;
        foreach (Rec?[] node in w.List(Lists.Bases))
        {
            Rec? b = node[0];
            if (!Objects.Valid(w, b) || Map.BaseForce(w, b!) != force.Index || b!.U16(0x4E) is < 52 or > 61) continue;
            BinaryPrimitives.WriteInt32LittleEndian(b.Bytes.AsSpan(0x3C), Math.Clamp(b.I32(0x3C), 0, Engagement.MaxTroops(w, b)));
        }
    }

    // 0x224CC0: losing panel 62 caps class-2 buildings, preserving the incomplete-building minus one.
    internal void ClampFacilityDurability(Rec force)
    {
        if (force.Index is < 1 or > 51 || (force.U8(0x1F6) & 0x20) == 0 || (force.U8(0x1D2) & 0x20) != 0) return;
        foreach (Rec?[] node in w.List(Lists.Facilities))
        {
            Rec? building = node[0];
            if (building is null || !Map.BuildingValid(building) || building.U8(0x13) != force.Index) continue;
            Rec kind = w.Entry(Tables.ObjectKinds, building.U8(0x12), 30);
            if (kind.Index == 0 || kind.U8(0x2C) != 2) continue;
            byte[] vector = kind.Vector50 ?? [];
            int max = (vector.Length & ~3) == 0x4C ? Objects.DecodeWord(BitConverter.ToUInt16(vector), 9, kind.Index) : kind.U16(0x2E);
            if ((building.U8(0x16) & 1) == 0) max--;
            Word(building, 0x14, Math.Clamp(Math.Min(building.U16(0x14), max), 0, 9999));
        }
    }

    // 0x26CCC0: reset advisor/appointments and their dependent caps, then refund the old doctrine
    // only when the ruler clan changes. Message 0x5800 is an explicit host notification boundary.
    internal void Apply(Rec? force, Rec? successor, Rec? oldRuler, bool notify)
    {
        if (!Objects.Valid(w, force) || !Objects.Valid(w, successor) || !Objects.Valid(w, oldRuler)) return;
        RequireInputs();
        Word(force!, 0x16A, Math.Clamp(ResetDays, 0, 65535));
        ClearAdvisor(w, force, true);
        for (int field = 0; field < 5; field++) ClearField(w, force, field);
        OfficerCapture.RefreshMorale(w, force!);
        OfficerCapture.RefreshPolicyTier(w, force!);
        OfficerCapture.RefreshDurability(w, force!);
        if (Objects.IsPlayerForce(w, force!) && notify) notifyPlayer?.Invoke();
        if (successor!.U8(0x14C) == oldRuler!.U8(0x14C)) return;
        foreach (int at in new[] { 0x70, 0x8E, 0xAC, 0xCA, 0xE8 }) force!.Bytes[at] = 0;
        RefundDoctrine(force!, oldRuler);
    }
}
