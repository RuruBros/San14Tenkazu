using System.Buffers.Binary;

namespace San14Tenkazu.Tests;

public sealed class RegisteredOfficerRelationTests
{
    private static Dictionary<int, RegisteredOfficer> People()
    {
        return Enumerable.Range(3001, 11).Select(id =>
        {
            var bytes = new byte[1117];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)id);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), 4);
            RegisteredOfficerFields.WriteText(bytes, RegisteredOfficerFields.Surname, "A");
            return new RegisteredOfficer(id - 3001, id, bytes);
        }).ToDictionary(o => o.Id);
    }
    private static void Set(Dictionary<int, RegisteredOfficer> people, FieldDef field, int value, params int[] ids)
    {
        foreach (int id in ids) field.Encode(value).CopyTo(people[id].Data, field.Offset);
    }
    private static void Apply(Dictionary<int, RegisteredOfficer> people, RegisteredOfficer[] changes)
    {
        foreach (RegisteredOfficer person in changes) people[person.Id] = person;
    }

    [Fact]
    public void SwornNumberDisplaysTheWholeGroupAndNeverItsOwnerAsAMember()
    {
        var people = People();
        Set(people, RegisteredOfficerFields.Sworn, 3001, 3001, 3002, 3010);
        Assert.Equal(new[] { 3002, 3010 }, RegisteredOfficerRelations.Members(people.Values, 3001, RegisteredOfficerFields.Sworn));
        Assert.Equal(new[] { 3001, 3010 }, RegisteredOfficerRelations.Members(people.Values, 3002, RegisteredOfficerFields.Sworn));
        Assert.Empty(RegisteredOfficerRelations.Members(people.Values, 3003, RegisteredOfficerFields.Sworn));
        Assert.Throws<ArgumentException>(() => RegisteredOfficerRelations.AddMember(people.Values, 3001, 3001, RegisteredOfficerFields.Sworn));
    }

    [Fact]
    public void GroupAddsJoinAnExistingGroupOrMoveAPersonAndDissolveAnAbandonedSwornPair()
    {
        var people = People();
        Set(people, RegisteredOfficerFields.Sworn, 3002, 3002, 3003);
        var joined = RegisteredOfficerRelations.AddMember(people.Values, 3001, 3002, RegisteredOfficerFields.Sworn);
        Assert.Equal(0, RegisteredOfficerFields.Sworn.Read(people[3001].Data)); // pure plan
        Apply(people, joined);
        Assert.Equal(new[] { 3002, 3003 }, RegisteredOfficerRelations.Members(people.Values, 3001, RegisteredOfficerFields.Sworn));
        Set(people, RegisteredOfficerFields.Sworn, 3004, 3004, 3005);
        Apply(people, RegisteredOfficerRelations.AddMember(people.Values, 3001, 3004, RegisteredOfficerFields.Sworn));
        Assert.Equal(new[] { 3002, 3003, 3004 }, RegisteredOfficerRelations.Members(people.Values, 3001, RegisteredOfficerFields.Sworn));
        Assert.Equal(0, RegisteredOfficerFields.Sworn.Read(people[3005].Data));
        Apply(people, RegisteredOfficerRelations.RemoveMember(people.Values, 3001, 3002, RegisteredOfficerFields.Sworn));
        Assert.Equal(3002, RegisteredOfficerFields.Sworn.Read(people[3001].Data)); // number is not a pointer to an indispensable owner
        Apply(people, RegisteredOfficerRelations.RemoveMember(people.Values, 3001, 3003, RegisteredOfficerFields.Sworn));
        Apply(people, RegisteredOfficerRelations.RemoveMember(people.Values, 3001, 3004, RegisteredOfficerFields.Sworn));
        Assert.All(new[] { 3001, 3002, 3003, 3004, 3005 }, id => Assert.Equal(0, RegisteredOfficerFields.Sworn.Read(people[id].Data)));
    }

    [Fact]
    public void ClanEditsChangeOnlyTheSharedClanWordsAndKeepSingletonFamilies()
    {
        var people = People();
        var before = people.ToDictionary(p => p.Key, p => (byte[])p.Value.Data.Clone());
        Apply(people, RegisteredOfficerRelations.AddMember(people.Values, 3001, 3002, RegisteredOfficerFields.Clan));
        Apply(people, RegisteredOfficerRelations.AddMember(people.Values, 3001, 3003, RegisteredOfficerFields.Clan));
        Assert.Equal(new[] { 3002, 3003 }, RegisteredOfficerRelations.Members(people.Values, 3001, RegisteredOfficerFields.Clan));
        Assert.All(people.Values, person => Assert.All(Enumerable.Range(0, person.Data.Length).Where(i => i is not 0x1D and not 0x1E),
            i => Assert.Equal(before[person.Id][i], person.Data[i])));
        Apply(people, RegisteredOfficerRelations.RemoveMember(people.Values, 3001, 3002, RegisteredOfficerFields.Clan));
        Apply(people, RegisteredOfficerRelations.RemoveMember(people.Values, 3001, 3003, RegisteredOfficerFields.Clan));
        Assert.Equal(3001, RegisteredOfficerFields.Clan.Read(people[3001].Data));
    }

    [Fact]
    public void SpouseReplacementClearsBothFormerPartnersAndSetsTheNewReciprocalPartner()
    {
        var people = People();
        Set(people, RegisteredOfficerFields.Spouse, 3002, 3001);
        Set(people, RegisteredOfficerFields.Spouse, 3001, 3002);
        Set(people, RegisteredOfficerFields.Spouse, 3004, 3003);
        Set(people, RegisteredOfficerFields.Spouse, 3003, 3004);
        var changes = RegisteredOfficerRelations.SpouseChanges(people.Values, 3001, 3003);
        Assert.DoesNotContain(changes, p => p.Id == 3001);
        Apply(people, changes);
        Assert.Equal(0, RegisteredOfficerFields.Spouse.Read(people[3002].Data));
        Assert.Equal(0, RegisteredOfficerFields.Spouse.Read(people[3004].Data));
        Assert.Equal(3001, RegisteredOfficerFields.Spouse.Read(people[3003].Data));
        Assert.Throws<ArgumentException>(() => RegisteredOfficerRelations.SpouseChanges(people.Values, 3001, 3001));
    }

    [Fact]
    public void LikesAndDislikesUseEightDistinctOneWaySlotsAndClearRemovedValues()
    {
        var people = People();
        foreach (var fields in new[] { RegisteredOfficerFields.Likes, RegisteredOfficerFields.Dislikes })
        {
            int[] ids = Enumerable.Range(3002, 8).ToArray();
            var updated = RegisteredOfficerRelations.SetTies(people[3001], fields, ids);
            Assert.Equal(ids.Select(i => (long)i), fields.Select(f => f.Read(updated.Data)));
            Assert.All(fields, f => Assert.Equal(0, f.Read(people[3002].Data)));
            Assert.Throws<ArgumentException>(() => RegisteredOfficerRelations.SetTies(updated, fields, ids.Append(3010)));
            Assert.Throws<ArgumentException>(() => RegisteredOfficerRelations.SetTies(updated, fields, new[] { 3002, 3002 }));
            Assert.Throws<ArgumentException>(() => RegisteredOfficerRelations.SetTies(updated, fields, new[] { 3001 }));
            updated = RegisteredOfficerRelations.SetTies(updated, fields, new[] { 3003 });
            Assert.Equal(3003, fields[0].Read(updated.Data));
            Assert.All(fields.Skip(1), f => Assert.Equal(0, f.Read(updated.Data)));
        }
    }

    [Fact]
    public void DirectSelfReferencesAndNewDuplicateSlotsAreRejected()
    {
        var people = People();
        RegisteredOfficer original = people[3001];
        foreach (var field in RegisteredOfficerRelations.Direct.Concat(RegisteredOfficerFields.Likes).Concat(RegisteredOfficerFields.Dislikes))
        {
            var edited = original with { Data = (byte[])original.Data.Clone() };
            field.Encode(3001).CopyTo(edited.Data, field.Offset);
            Assert.Throws<ArgumentException>(() => RegisteredOfficerRelations.Validate(edited, original));
        }
        foreach (var fields in new[] { RegisteredOfficerFields.Likes, RegisteredOfficerFields.Dislikes, RegisteredOfficerFields.Personalities, RegisteredOfficerFields.Tactics })
        {
            var edited = original with { Data = (byte[])original.Data.Clone() };
            foreach (var field in fields.Take(2)) field.Encode(1).CopyTo(edited.Data, field.Offset);
            Assert.Throws<ArgumentException>(() => RegisteredOfficerRelations.Validate(edited, original));
            RegisteredOfficerRelations.Validate(edited, edited); // an existing file remains readable and unchanged
        }
    }
}
