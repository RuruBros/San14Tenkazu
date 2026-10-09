using System.Buffers.Binary;

namespace San14Tenkazu.Tests;

public sealed class RegisteredOfficerTests
{
    private static byte[] Fixture()
    {
        var data = new byte[RegisteredOfficerFields.Stride * RegisteredOfficerFields.Slots];
        for (int slot = 0; slot < RegisteredOfficerFields.Slots; slot++)
        {
            var record = new byte[RegisteredOfficerFields.Stride];
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), 4);
            foreach (var f in RegisteredOfficerFields.Abilities) f.Encode(50).CopyTo(record, f.Offset);
            if (slot < 11)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)(RegisteredOfficerFields.FirstId + slot));
                RegisteredOfficerFields.WriteText(record, RegisteredOfficerFields.Surname, "A");
                RegisteredOfficerFields.WriteText(record, RegisteredOfficerFields.GivenName, (slot + 1).ToString());
                record[0x27] = 0x5A;
            }
            record.CopyTo(data, slot * RegisteredOfficerFields.Stride);
        }
        return data;
    }

    [Fact]
    public void AllRegisteredFieldsRoundTripWithoutTouchingOtherSlotsOrReservedBytes()
    {
        byte[] before = Fixture();
        var file = new RegisteredOfficerFile("fixture.s14", before);
        RegisteredOfficer first = file.Officers[0] with { Data = (byte[])file.Officers[0].Data.Clone() };
        var expected = RegisteredOfficerFields.Numbers.ToDictionary(f => f, f => f == RegisteredOfficerFields.Formations ? (1L << 21) - 1 : f.Max);
        foreach (FieldDef field in RegisteredOfficerFields.Numbers) field.Encode(expected[field]).CopyTo(first.Data, field.Offset);
        foreach (var fields in new[] { RegisteredOfficerFields.Likes, RegisteredOfficerFields.Dislikes, RegisteredOfficerFields.Personalities, RegisteredOfficerFields.Tactics })
            for (int i = 0; i < fields.Length; i++)
            {
                expected[fields[i]] -= i;
                fields[i].Encode(expected[fields[i]]).CopyTo(first.Data, fields[i].Offset);
            }
        foreach (var field in RegisteredOfficerFields.Personalities.Skip(RegisteredOfficerFields.PersonalityActiveMax))
        {
            expected[field] = 0;
            field.Encode(0).CopyTo(first.Data, field.Offset);
        }
        RegisteredOfficerFields.WriteText(first.Data, RegisteredOfficerFields.Surname, "AB");
        RegisteredOfficerFields.WriteText(first.Data, RegisteredOfficerFields.StyleName, "CD");
        RegisteredOfficerFields.WriteText(first.Data, RegisteredOfficerFields.Biography, new string('x', 500));
        byte[] after = file.Compose([first]);
        var reread = RegisteredOfficerFile.Parse(after);
        Assert.Equal(11, reread.Length);
        Assert.Equal("AB1", reread[0].Name);
        Assert.Equal(500, RegisteredOfficerFields.ReadText(reread[0].Data, RegisteredOfficerFields.Biography).Length);
        Assert.All(RegisteredOfficerFields.Numbers, f => Assert.Equal(expected[f], f.Read(reread[0].Data)));
        Assert.Equal(0x5A, reread[0].Data[0x27]);
        Assert.Equal(before[RegisteredOfficerFields.Stride..], after[RegisteredOfficerFields.Stride..]);
    }

    [Fact]
    public void AbilitiesOnlyChangeTheirFiveBytesAndKeepTheRequestedTotal()
    {
        var file = new RegisteredOfficerFile("fixture.s14", Fixture());
        var officer = file.Officers[3] with { Data = (byte[])file.Officers[3].Data.Clone() };
        int[] values = [92, 87, 92, 88, 91];
        for (int i = 0; i < 5; i++) RegisteredOfficerFields.Abilities[i].Encode(values[i]).CopyTo(officer.Data, RegisteredOfficerFields.Abilities[i].Offset);
        byte[] after = file.Compose([officer]);
        Assert.Equal(450, RegisteredOfficerFile.Parse(after)[3].Total);
        int[] changed = Enumerable.Range(0, after.Length).Where(i => after[i] != file.Expected[i]).ToArray();
        Assert.Equal(Enumerable.Range(officer.Slot * RegisteredOfficerFields.Stride + RegisteredOfficerFields.AbilityOffset, 5), changed);
    }

    [Fact]
    public void NewPersonalitiesAreLimitedToFiveAcrossAllNineStorageSlots()
    {
        var file = new RegisteredOfficerFile("fixture.s14", Fixture());
        RegisteredOfficer officer = file.Officers[0] with { Data = (byte[])file.Officers[0].Data.Clone() };
        // Empty earlier storage slots are compacted by the native conversion, so
        // a valid trait in storage slot nine still counts towards the five.
        foreach (int index in new[] { 0, 2, 4, 6, 8 })
            RegisteredOfficerFields.Personalities[index].Encode(index + 1).CopyTo(officer.Data, RegisteredOfficerFields.Personalities[index].Offset);
        _ = file.Compose([officer]);
        RegisteredOfficerFields.Personalities[1].Encode(2).CopyTo(officer.Data, RegisteredOfficerFields.Personalities[1].Offset);
        Assert.Throws<ArgumentException>(() => file.Compose([officer]));
    }

    [Fact]
    public void ExistingNinePersonalitiesRemainIntactDuringUnrelatedEdits()
    {
        byte[] before = Fixture();
        for (int k = 0; k < 9; k++) RegisteredOfficerFields.Personalities[k].Encode(k + 1).CopyTo(before, RegisteredOfficerFields.Personalities[k].Offset);
        var file = new RegisteredOfficerFile("fixture.s14", before);
        RegisteredOfficer officer = file.Officers[0] with { Data = (byte[])file.Officers[0].Data.Clone() };
        RegisteredOfficerFields.Abilities[0].Encode(100).CopyTo(officer.Data, RegisteredOfficerFields.Abilities[0].Offset);
        byte[] after = file.Compose([officer]);
        Assert.Equal(new[] { RegisteredOfficerFields.AbilityOffset }, Enumerable.Range(0, before.Length).Where(i => before[i] != after[i]));
    }

    [Fact]
    public void InvalidFormatReadonlyBytesAndOutOfRangeEditsAreRejected()
    {
        byte[] data = Fixture();
        Assert.Throws<InvalidDataException>(() => RegisteredOfficerFile.Parse(data[..^1]));
        data[2] = 3;
        Assert.Throws<InvalidDataException>(() => RegisteredOfficerFile.Parse(data));
        var file = new RegisteredOfficerFile("fixture.s14", Fixture());
        var officer = file.Officers[0] with { Data = (byte[])file.Officers[0].Data.Clone() };
        officer.Data[0x27]++;
        Assert.Throws<InvalidDataException>(() => file.Compose([officer]));
        officer.Data[0x27]--;
        officer.Data[RegisteredOfficerFields.AbilityOffset] = 101;
        Assert.Throws<ArgumentOutOfRangeException>(() => file.Compose([officer]));
        Assert.Throws<ArgumentException>(() => RegisteredOfficerFields.WriteText(officer.Data, RegisteredOfficerFields.Surname, "ABC"));
        Assert.Throws<ArgumentException>(() => RegisteredOfficerFields.WriteText(officer.Data, RegisteredOfficerFields.Biography, "bad\0text"));
    }

    [Fact]
    public void SavingKeepsAnExactBackupAndRefusesAStaleSourceOrDeletedOfficer()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tenkazu-registered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, RegisteredOfficerFile.FileName);
        try
        {
            byte[] before = Fixture();
            File.WriteAllBytes(path, before);
            var file = RegisteredOfficerFile.Read(path, static () => false);
            var officer = file.Officers[0] with { Data = (byte[])file.Officers[0].Data.Clone() };
            officer.Data[RegisteredOfficerFields.AbilityOffset] = 100;
            RegisteredOfficerFields.Face.Encode(60000).CopyTo(officer.Data, 0x16);
            RegisteredOfficerFields.Clan.Encode(3001).CopyTo(officer.Data, 0x1D);
            officer = RegisteredOfficerRelations.SetTies(officer, RegisteredOfficerFields.Likes, new[] { 3002, 3003 });
            RegisteredOfficerFields.WriteText(officer.Data, RegisteredOfficerFields.Biography, "저장 확인");
            var other = file.Officers[1] with { Data = (byte[])file.Officers[1].Data.Clone() };
            RegisteredOfficerFields.Clan.Encode(3001).CopyTo(other.Data, 0x1D);
            byte[] after = file.Compose([officer, other]);
            string backup = file.Save(after, Path.Combine(directory, "backups"));
            Assert.Equal(before, File.ReadAllBytes(backup));
            Assert.Equal(after, File.ReadAllBytes(path));
            var reread = RegisteredOfficerFile.Read(path);
            Assert.Equal(new[] { 3002 }, RegisteredOfficerRelations.Members(reread.Officers, 3001, RegisteredOfficerFields.Clan));
            Assert.Equal(60000, RegisteredOfficerFields.Face.Read(reread.Officers[0].Data));
            Assert.Equal("저장 확인", RegisteredOfficerFields.ReadText(reread.Officers[0].Data, RegisteredOfficerFields.Biography));
            Assert.Throws<InvalidOperationException>(() => file.Save(after, Path.Combine(directory, "backups")));
            var current = RegisteredOfficerFile.Read(path);
            byte[] deleted = (byte[])after.Clone();
            BinaryPrimitives.WriteUInt16LittleEndian(deleted, 0);
            Assert.Throws<InvalidDataException>(() => current.Save(deleted, Path.Combine(directory, "backups")));
            Assert.Equal(after, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("SAN14CE_KO", true)]
    [InlineData("SAN14PK_EN", true)]
    [InlineData("SAN14", true)]
    [InlineData("San14Tenkazu", false)]
    [InlineData("San14Tenkazu.Tests", false)]
    [InlineData("SAN14_Launcher", false)]
    public void OnlyTheGameProcessesBlockSaving(string name, bool expected) => Assert.Equal(expected, RegisteredOfficerFile.IsGameProcessName(name));

    [Fact]
    public void BackupFailureLeavesTheSourceUntouched()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tenkazu-registered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, RegisteredOfficerFile.FileName);
            byte[] before = Fixture();
            File.WriteAllBytes(path, before);
            var file = RegisteredOfficerFile.Read(path, static () => false);
            var officer = file.Officers[0] with { Data = (byte[])file.Officers[0].Data.Clone() };
            officer.Data[RegisteredOfficerFields.AbilityOffset] = 100;
            string blockedDirectory = Path.Combine(directory, "not-a-directory");
            File.WriteAllText(blockedDirectory, "occupied");
            Assert.Throws<IOException>(() => file.Save(file.Compose([officer]), blockedDirectory));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(directory, ".tenkazu-*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void SavingRechecksTheGameStateBeforeCreatingAnyBackupOrTemporaryFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), "tenkazu-registered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, RegisteredOfficerFile.FileName);
        try
        {
            byte[] before = Fixture();
            File.WriteAllBytes(path, before);
            bool running = false;
            var file = RegisteredOfficerFile.Read(path, () => running);
            var officer = file.Officers[0] with { Data = (byte[])file.Officers[0].Data.Clone() };
            officer.Data[RegisteredOfficerFields.AbilityOffset] = 100;
            byte[] replacement = file.Compose([officer]);
            running = true;
            Assert.Throws<InvalidOperationException>(() => file.Save(replacement, Path.Combine(directory, "backups")));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.False(Directory.Exists(Path.Combine(directory, "backups")));
            Assert.Empty(Directory.GetFiles(directory, ".tenkazu-*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void OfflineChoicesUseTheValidatedGameNames()
    {
        Assert.Equal("왕도", RegisteredOfficerNames.Get("doctrine").Single(n => n.Id == 1).Name);
        Assert.Equal("어린", RegisteredOfficerNames.Get("formation").Single(n => n.Id == 1).Name);
        Assert.Equal("관우", RegisteredOfficerNames.Get("officer").Single(n => n.Id == 147).Name);
        Assert.All(RegisteredOfficerNames.Get("personality"), n => Assert.InRange(n.Id, 1, 355));
        Assert.Equal(171, RegisteredOfficerNames.Get("registered-personality").Length);
        Assert.Equal(126, RegisteredOfficerNames.Get("registered-tactic").Length);
        Assert.DoesNotContain(RegisteredOfficerNames.Get("registered-personality"), n => n.Id is 79 or 82 or 100 or 220);
        Assert.DoesNotContain(RegisteredOfficerNames.Get("registered-tactic"), n => n.Id is 27 or 174 or 200);
    }

    [Fact]
    public void NativePackedRecordDecodesAdjacentFieldsWithoutMemoryLayoutOffsets()
    {
        byte[] bytes = Fixture();
        using var stream = new MemoryStream(bytes, true);
        using var writer = new BinaryWriter(stream, System.Text.Encoding.Unicode, true);
        writer.Write((ushort)3001); writer.Write((ushort)4);
        foreach (string text in new[] { "설", "지", "은월" })
            writer.Write(System.Text.Encoding.Unicode.GetBytes(text.PadRight(3, '\0')));
        writer.Write((ushort)54321); writer.Write((byte)1);
        foreach (ushort number in new ushort[] { 189, 251, 65000, 147, 148, 149, 3001, 0xA55A }) writer.Write(number);
        writer.Write((byte)149);
        for (ushort id = 1; id <= 16; id++) writer.Write(id);
        writer.Write(new byte[] { 61, 62, 63, 64, 65 });
        for (ushort id = 1; id <= 9; id++) writer.Write(id);
        writer.Write((uint)((1 << 13) | (1 << 14) | (1 << 20)));
        writer.Write(Enumerable.Range(1, 10).Select(i => (byte)i).ToArray());
        writer.Write(new byte[] { 6, 120, 6, 13 });
        Assert.Equal(0x73, stream.Position);
        writer.Write(System.Text.Encoding.Unicode.GetBytes(new string('가', 500) + '\0'));
        Assert.Equal(1117, stream.Position);
        var file = new RegisteredOfficerFile("fixture.s14", bytes);
        byte[] record = file.Officers[0].Data;
        Assert.Equal("설지", file.Officers[0].Name);
        Assert.Equal("은월", RegisteredOfficerFields.ReadText(record, RegisteredOfficerFields.StyleName));
        Assert.Equal(54321, RegisteredOfficerFields.Face.Read(record));
        Assert.Equal(1, RegisteredOfficerFields.Gender.Read(record));
        Assert.Equal(new long[] { 189, 251, 65000, 147, 148, 149, 3001, 149 },
            new[] { RegisteredOfficerFields.Birth, RegisteredOfficerFields.Death, RegisteredOfficerFields.Clan,
                RegisteredOfficerFields.Father, RegisteredOfficerFields.Mother, RegisteredOfficerFields.Spouse,
                RegisteredOfficerFields.Sworn, RegisteredOfficerFields.Compatibility }.Select(f => f.Read(record)));
        Assert.Equal(Enumerable.Range(1, 8).Select(i => (long)i), RegisteredOfficerFields.Likes.Select(f => f.Read(record)));
        Assert.Equal(Enumerable.Range(9, 8).Select(i => (long)i), RegisteredOfficerFields.Dislikes.Select(f => f.Read(record)));
        Assert.Equal(Enumerable.Range(61, 5).Select(i => (long)i), RegisteredOfficerFields.Abilities.Select(f => f.Read(record)));
        Assert.Equal(Enumerable.Range(1, 9).Select(i => (long)i), RegisteredOfficerFields.Personalities.Select(f => f.Read(record)));
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (long)i), RegisteredOfficerFields.Tactics.Select(f => f.Read(record)));
        Assert.Equal(new long[] { 6, 120, 6, 13 }, new[] { RegisteredOfficerFields.Ideology, RegisteredOfficerFields.Policy,
            RegisteredOfficerFields.PolicyLevel, RegisteredOfficerFields.Voice }.Select(f => f.Read(record)));
        Assert.Equal(500, RegisteredOfficerFields.ReadText(record, RegisteredOfficerFields.Biography).Length);
        Assert.Equal(bytes, file.Compose(file.Officers));
    }

    [Fact]
    public void ExistingUnsupportedValuesAndTextPaddingSurviveUnrelatedEdits()
    {
        byte[] bytes = Fixture();
        bytes[RegisteredOfficerFields.AbilityOffset + 4] = 201;
        bytes[RegisteredOfficerFields.Voice.Offset] = 16;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x61), 0x80004000);
        // Nonzero trailing data after a valid name's terminator is retained.
        bytes[8] = 0x5A;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(0x1F), 3001);
        var file = new RegisteredOfficerFile("fixture.s14", bytes);
        var officer = file.Officers[0] with { Data = (byte[])file.Officers[0].Data.Clone() };
        officer.Data[RegisteredOfficerFields.AbilityOffset] = 99;
        RegisteredOfficerFields.WriteText(officer.Data, RegisteredOfficerFields.Surname, "A");
        byte[] after = file.Compose([officer]);
        Assert.Equal(new[] { RegisteredOfficerFields.AbilityOffset }, Enumerable.Range(0, bytes.Length).Where(i => bytes[i] != after[i]));
        RegisteredOfficerFields.Formations.Encode(0x80004002).CopyTo(officer.Data, 0x61);
        after = file.Compose([officer]);
        Assert.Equal(0x80004002u, BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(0x61)));
        RegisteredOfficerFields.Formations.Encode(0x40004002).CopyTo(officer.Data, 0x61);
        Assert.Throws<InvalidDataException>(() => file.Compose([officer]));
    }
}
