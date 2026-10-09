using System.Buffers.Binary;

namespace San14Tenkazu.Formulas;

// 0x295AA0 ordinary officer death, player notices and surviving-force corps succession.
// Ruler succession and physical UI playback remain separate bodies.
internal sealed class OfficerDeath(World w, OfficerLifecycle officers, CommandStorage commands, UnitDestruction destruction)
{
    [Rva(0x1A488AC)] internal const int GeneralPopularityLoss = 2;
    [Rva(0x1A499FC)] internal const int NoticeOfficeGrade = 2;
    [Rva(0x1A49A1C)] internal const int OrdinaryRelationLoss = 5;
    private static void Word(Rec p, int at, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(p.Bytes.AsSpan(at), unchecked((ushort)value));

    internal int ApplyOrdinaryWithoutItems(Rec p, bool destroyOwnUnit)
        => ApplyCore(p, destroyOwnUnit, null);

    internal int ApplyOrdinary(Rec p, bool destroyOwnUnit, DeathInheritance inheritance, Rec? attacker = null)
        => ApplyCore(p, destroyOwnUnit, inheritance, attacker);

    internal int ApplyPlayerOrdinary(Rec p, int mode, bool includeHistory, OfficerDeathNotice notice)
        => ApplyCore(p, false, null, notice: notice, noticeMode: mode, includeHistory: includeHistory);

    internal int ApplyCorpsLeader(Rec p, DeathInheritance inheritance, CorpsLifecycle lifecycle, Rec? attacker = null,
        bool destroyOwnUnit = false)
    {
        if (p.U8(0xE6) != 2) throw new NotSupportedException("This death entry requires a corps leader.");
        return ApplyCore(p, destroyOwnUnit, inheritance, attacker, lifecycle);
    }

    private int ApplyCore(Rec p, bool destroyOwnUnit, DeathInheritance? inheritance, Rec? attacker = null,
        CorpsLifecycle? lifecycle = null, OfficerDeathNotice? notice = null, int noticeMode = 0, bool includeHistory = false)
    {
        int status = p.U8(0xE6);
        if (!Objects.PersonValid(p) || status is not (2 or 3 or 4 or 5 or 6 or 9) || status == 2 && lifecycle is null)
            throw new NotSupportedException("This death body requires an ordinary officer or verified corps succession.");
        Rec force = Objects.ForceOf(w, p);
        if (status == 2 && (!Objects.CorpsValid(w.Entry(Tables.Corps, p.U8(0xE0), 51)) ||
            !Objects.ForceValid(force) || Capture.CityCount(w, force) == 0 ||
            !w.List(Lists.OfficerList).Select(n => n[0]).OfType<Rec>().Any(other => other != p &&
                other.U8(0xE6) is >= 1 and <= 4 && Objects.ForceOf(w, other) == force && OfficerCapture.CountedOfficer(w, other))))
            throw new NotSupportedException("This corps-leader death requires a surviving original force.");
        if (attacker is not null && attacker.Table != Tables.Officers)
            throw new NotSupportedException("The death attacker must be an officer pointer.");
        if (Objects.Valid(w, attacker) && Objects.ForceOf(w, attacker!) is var attackerForce &&
            (Objects.IsPlayerForce(w, attackerForce) || (uint)(attackerForce.Index - 46) < 5 || (uint)(force.Index - 46) < 5))
            throw new NotSupportedException("Player or tribe attackers require their separate history and response bodies.");
        Rec home = Map.Base(w, p.U16(0xE2))!;
        Rec office = w.Table(Tables.Offices)[p.U8(0xE7)];
        Rec leader = w.OfficerById(p.U16(0xBC)) ?? throw new InvalidDataException("Missing sworn leader pointer.");
        Rec? location = CommandTravel.OfficerLocation(w, commands, p);
        if ((notice is null && Objects.IsPlayerForce(w, force)) || Objects.Valid(w, office) && office.U8(0x37) < NoticeOfficeGrade ||
            status == 6 && location is not null && Objects.IsPlayerForce(w, w.Entry(Tables.Forces, location.U8(0x14), 51)))
            throw new NotSupportedException("This death body requires the separate presentation prefix.");
        bool hasItems = Objects.ItemsOf(w, p).Any();
        if (inheritance is null && hasItems)
            throw new NotSupportedException("Owned items require the death-inheritance body.");
        if (status == 6 && hasItems && Objects.IsPlayerForce(w, w.Entry(Tables.Forces, Map.BaseForce(w, home), 51)))
            throw new NotSupportedException("Prisoner items in a player base require the separate history body.");
        bool marked = (p.U16(0x15E) & 0x4000) != 0;
        if (!marked || status == 5) officers.RequireResetName(p);
        if (notice is not null)
        {
            if (commands.FindOfficer(p.U16(0x10)) is not null)
                throw new NotSupportedException("Player command cancellation requires its separate notice and refresh body.");
            Rec narrator = notice.Prepare(p, force, office, noticeMode, includeHistory);
            if (location is null) throw new InvalidDataException("A player death notice requires the copied officer location.");
            notice.Emit(p, force, office, location, narrator, noticeMode, includeHistory);
        }
        int fame = !marked && !Objects.SpecialOfficer(w, p, false) ? p.U8(0x1BC) : 0;
        if (status is 2 or 3 or 4 && Objects.ForceValid(force))
        {
            OfficerCapture.RemoveAppointments(w, p, force);
            OfficerCapture.RemoveForcePosts(w, p, force);
            if (Objects.IsPlayerForce(w, force)) notice?.ClearPlayerSlots(p);
        }
        Rec unit = Objects.UnitOf(w, p);
        if (destroyOwnUnit && Objects.UnitValid(unit) && !Engaged(unit)) destruction.ApplyUnopposedRemoval(unit);
        var command = commands.FindOfficer(p.U16(0x10));
        if (command is not null) commands.RemoveOfficer(command, p.U16(0x10));
        if (Objects.Valid(w, attacker)) ForceRelations.ChangeNpc(w, force, Objects.ForceOf(w, attacker!), -OrdinaryRelationLoss);
        Rec? retainedCorps = null;
        if (status == 2)
        {
            Rec corps = w.Entry(Tables.Corps, p.U8(0xE0), 51);
            // 0x2301F0(corps,0,0) still counts the dying leader and includes marked serving officers.
            int serving = w.List(Lists.OfficerList).Select(n => n[0]).OfType<Rec>().Count(other =>
                corps.Index is >= 1 and <= 51 && other.U8(0xE0) == corps.Index && other.U8(0xE6) is >= 1 and <= 4);
            if (serving == 1) lifecycle!.Merge(corps, CorpsLifecycle.MainCorps(w, force), 0);
            else retainedCorps = corps;
        }
        if (status == 6)
        {
            Rec homeForce = w.Entry(Tables.Forces, Map.BaseForce(w, home), 51);
            foreach (Rec item in Objects.ItemsOf(w, p))
            {
                item.Bytes[0x4F] &= 0xF7;
                Word(item, 0x4A, Math.Clamp(homeForce.Index == 0 ? 5014 : homeForce.U16(0x10), 0, 6000));
                item.Bytes[0x50] = 1;
            }
        }
        // 0x233650(person,5) is a status comparison. A free marked officer follows actual death, not QueueFree.
        if ((p.U16(0x15E) & 0x4000) != 0 && p.U8(0xE6) != 5)
        {
            officers.QueueFree(p);
            return 0;
        }
        inheritance?.Apply(p, attacker, command);
        officers.ResetSlot(p);
        p.Bytes[0xE6] = 9; // 0x241C30 clamps its status argument into 0..9.
        if (Objects.ForceValid(force) && force.Index != 51 && (uint)(force.Index - 46) > 4)
            Word(force, 0x1BE, Math.Clamp(force.I16(0x1BE) - Math.Max(1, fame) * GeneralPopularityLoss, -1000, 1000));
        var sworn = new List<Rec>();
        foreach (Rec other in w.Table(Tables.Officers))
        {
            int group = other.U16(0xBC);
            if ((uint)(group - 1) < 6000 && group == leader.U16(0x10) && other.U16(0x10) != p.U16(0x10)) sworn.Add(other);
            if (other.U16(0xBA) == p.U16(0x10)) Word(other, 0xBA, 0);
            if (OfficerRelations.Likes(w, other, p)) OfficerRelations.RemoveLiked(w, other, p);
            if (OfficerRelations.Dislikes(w, other, p)) OfficerRelations.RemoveDisliked(w, other, p);
        }
        if (sworn.Count == 1) Word(sworn[0], 0xBC, 0);
        else if (sworn.Count > 1 && leader.U16(0x10) == p.U16(0x10))
        {
            OfficerRelations.SortSworn(sworn);
            int id = Math.Min(sworn[0].U16(0x10), (ushort)6000);
            foreach (Rec other in sworn) Word(other, 0xBC, id);
        }
        if (Objects.Valid(w, home)) OfficerCapture.AssignGovernor(w, home, OfficerCapture.SelectGovernor(w, home));
        if (retainedCorps is not null) lifecycle!.CheckSurvivingForce(retainedCorps);
        w.List(Lists.OfficerList).RemoveAll(n => ReferenceEquals(n[0], p));
        return 0;
    }

    // 0x233440 counts pending final removal and deferred destruction, including a normal attacker of a base.
    private bool Engaged(Rec unit) => w.List(Lists.EngagedUnits).Any(n => ReferenceEquals(n[0], unit)) ||
        w.List(Lists.Engagements).Any(n => Objects.Valid(w, n[0]) && ReferenceEquals(n[0], unit) ||
            Objects.Valid(w, n[1]) && Objects.Valid(w, n[2]) && ReferenceEquals(n[2], unit) &&
            Objects.UnitForce(w, n[2]!) is var f && f != 51 && (uint)(f - 46) > 4);
}
