namespace San14Tenkazu;

// CEditPersonData +0x50/+0x58 become CPersonData +0xB2/+0xBC (0x20A800).
// These are shared group numbers, not another officer's ID. The native predicates
// (0x2C9BD0, 0x2AFAC0) compare equal nonzero groups and exclude the same officer.
internal static class RegisteredOfficerRelations
{
    internal static readonly FieldDef[] Direct = [RegisteredOfficerFields.Father, RegisteredOfficerFields.Mother, RegisteredOfficerFields.Spouse];

    internal static int[] Members(IEnumerable<RegisteredOfficer> officers, int self, FieldDef group)
    {
        RegisteredOfficer[] all = officers.ToArray();
        long value = group.Read(all.Single(o => o.Id == self).Data);
        return value == 0 ? [] : all.Where(o => o.Id != self && group.Read(o.Data) == value).OrderBy(o => o.Slot).Select(o => o.Id).ToArray();
    }

    internal static RegisteredOfficer[] AddMember(IEnumerable<RegisteredOfficer> officers, int self, int member, FieldDef group)
    {
        if (self == member) throw new ArgumentException(L.Registered.SelfRelation);
        var all = officers.ToDictionary(o => o.Id);
        RegisteredOfficer owner = all[self], other = all[member];
        long value = group.Read(owner.Data);
        if (value == 0) value = group.Read(other.Data) is > 0 and var theirs ? theirs : self;
        long before = group.Read(other.Data);
        var changed = new Dictionary<int, RegisteredOfficer>();
        void Set(RegisteredOfficer person, long next)
        {
            if (group.Read(person.Data) == next) return;
            byte[] data = (byte[])person.Data.Clone();
            group.Encode(next).CopyTo(data, group.Offset);
            changed[person.Id] = person with { Data = data };
        }
        Set(owner, value);
        Set(other, value);
        // Match the live officer editor: an abandoned one-person sworn group ends.
        if (group == RegisteredOfficerFields.Sworn && before != 0 && before != value)
        {
            var left = all.Values.Where(o => o.Id != member && group.Read(o.Data) == before).ToArray();
            if (left.Length == 1) Set(left[0], 0);
        }
        return changed.Values.ToArray();
    }

    internal static RegisteredOfficer[] RemoveMember(IEnumerable<RegisteredOfficer> officers, int self, int member, FieldDef group)
    {
        var all = officers.ToDictionary(o => o.Id);
        if (!Members(all.Values, self, group).Contains(member)) return [];
        var changed = new List<RegisteredOfficer>();
        void Clear(RegisteredOfficer person)
        {
            byte[] data = (byte[])person.Data.Clone();
            group.Encode(0).CopyTo(data, group.Offset);
            changed.Add(person with { Data = data });
        }
        Clear(all[member]);
        if (group == RegisteredOfficerFields.Sworn && Members(all.Values, self, group).Length == 1) Clear(all[self]);
        return changed.ToArray();
    }

    internal static RegisteredOfficer SetTies(RegisteredOfficer officer, FieldDef[] fields, IEnumerable<int> members)
    {
        int[] ids = members.ToArray();
        if (ids.Length > fields.Length) throw new ArgumentException(L.Registered.RelationLimit(fields.Length));
        if (ids.Contains(officer.Id)) throw new ArgumentException(L.Registered.SelfRelation);
        if (ids.Distinct().Count() != ids.Length) throw new ArgumentException(L.Registered.DuplicateRelation);
        byte[] data = (byte[])officer.Data.Clone();
        for (int i = 0; i < fields.Length; i++) fields[i].Encode(i < ids.Length ? ids[i] : 0).CopyTo(data, fields[i].Offset);
        return officer with { Data = data };
    }

    // Registered partners are updated on both sides, as in OfficerEdits.Plan. Historical
    // officers' originals are outside this file, so only existing registered records are returned.
    internal static RegisteredOfficer[] SpouseChanges(IEnumerable<RegisteredOfficer> officers, int self, int spouse)
    {
        if (spouse == self) throw new ArgumentException(L.Registered.SelfRelation);
        var all = officers.ToDictionary(o => o.Id);
        FieldDef field = RegisteredOfficerFields.Spouse;
        var changes = new Dictionary<int, RegisteredOfficer>();
        void Set(int id, int next)
        {
            if (id == self || !all.TryGetValue(id, out var person) || field.Read(person.Data) == next) return;
            byte[] data = (byte[])person.Data.Clone();
            field.Encode(next).CopyTo(data, field.Offset);
            changes[id] = person with { Data = data };
        }
        int before = (int)field.Read(all[self].Data);
        if (all.TryGetValue(before, out var old) && field.Read(old.Data) == self) Set(before, 0);
        if (all.TryGetValue(spouse, out var partner))
        {
            int theirs = (int)field.Read(partner.Data);
            if (all.TryGetValue(theirs, out var former) && field.Read(former.Data) == spouse) Set(theirs, 0);
            Set(spouse, self);
        }
        return changes.Values.ToArray();
    }

    internal static void Validate(RegisteredOfficer officer, RegisteredOfficer original)
    {
        foreach (var field in Direct.Concat(RegisteredOfficerFields.Likes).Concat(RegisteredOfficerFields.Dislikes))
            if (field.Read(officer.Data) == officer.Id && field.Read(original.Data) != officer.Id) throw new ArgumentException(L.Registered.SelfRelation);
        foreach (var fields in new[] { RegisteredOfficerFields.Likes, RegisteredOfficerFields.Dislikes, RegisteredOfficerFields.Personalities, RegisteredOfficerFields.Tactics })
        {
            if (fields.All(f => f.Read(officer.Data) == f.Read(original.Data))) continue;
            long[] ids = fields.Select(f => f.Read(officer.Data)).Where(id => id != 0).ToArray();
            if (ids.Distinct().Count() != ids.Length) throw new ArgumentException(L.Registered.DuplicateSlot);
            if (fields == RegisteredOfficerFields.Personalities && ids.Length > RegisteredOfficerFields.PersonalityActiveMax)
                throw new ArgumentException(L.Registered.PersonalityLimit);
        }
    }
}
