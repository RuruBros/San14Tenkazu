using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace San14Tenkazu.Tests;

public sealed class RuleFileExportTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory().FullName;
    private string Input => Path.Combine(root, "input.s14");
    private string Profile => Path.Combine(root, "rule_profiles.json");
    private readonly byte[] header = new byte[RuleFileContainer.Block];
    private readonly byte[] body;
    private readonly RuleFileSnapshot baseline;
    private static readonly RuleKey Attack = new("formation", 1, "attack"), Defense = new("formation", 2, "defense"),
        Kind = new("tactic", 1, "kind1"), Amount = new("tactic", 1, "amount1"), Link = new("policy", 1, "effect1"),
        Effect = new("policyeffect", 2, "level10");

    public RuleFileExportTests()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream, Encoding.UTF8, true);
        w.Write(102); w.Write(12); w.Write(new byte[430]);
        w.Write(11); w.Write(21); w.Write(new byte[21 * 152]);
        foreach (var l in FileRuleLayout.All)
        {
            w.Write(l.Preceding); w.Write(l.Count);
            byte[] table = new byte[l.Count * l.Stride];
            for (int id = 1; id <= 3; id++) Encoding.Unicode.GetBytes($"R{id}").CopyTo(table, id * l.Stride);
            w.Write(table);
            w.Write(l.Preceding + 1); w.Write(l.FollowingCount);
        }
        body = stream.ToArray();
        header[0] = 102; "SN14SCEXVER0001"u8.CopyTo(header.AsSpan(4));
        RuleFileContainer.KoreanTitle.CopyTo(header.AsSpan(0x1a6));
        WriteInput(body);
        baseline = RuleFileSnapshot.Read(GameBuildFacts.ExeSha256, FormationFileData.Read(Input), body);
    }

    private void WriteInput(byte[] raw) => File.WriteAllBytes(Input, RuleFileContainer.Pack(header, raw));
    private void ProfileWith(params (RuleKey Key, int Desired)[] values)
    {
        var changes = values.Select(v => new RuleChange(v.Key, baseline.Profile.Default(v.Key), v.Desired));
        File.WriteAllBytes(Profile, JsonSerializer.SerializeToUtf8Bytes(baseline.Profile.WithChanges(changes)));
    }

    [Fact]
    public void AllFourTablesAndOriginalAreVerifiedAndAlreadyAppliedIsByteExact()
    {
        ProfileWith((Attack, 17), (Amount, 22), (Link, 2), (Effect, 0xabcd));
        var original = File.ReadAllBytes(Input);
        var plan = RuleFileExport.Preview(Input, Profile);
        Assert.All(plan.Edits, e => Assert.Equal(FileEditStatus.Apply, e.Status));
        var result = plan.Save(root, null);
        Assert.Equal(original, File.ReadAllBytes(Input));
        Assert.Equal(original, File.ReadAllBytes(result.Original));
        byte[] expected = body.ToArray();
        expected[baseline.Cells[Attack].Offset] = 17; expected[baseline.Cells[Amount].Offset] = 22;
        expected[baseline.Cells[Link].Offset] = 2;
        BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(baseline.Cells[Effect].Offset), 0xabcd);
        Assert.Equal(expected, LwcCodec.Decode(File.ReadAllBytes(result.File), RuleFileContainer.Block));
        var again = RuleFileExport.Preview(result.File, Profile);
        Assert.All(again.Edits, e => Assert.Equal(FileEditStatus.Already, e.Status));
        Assert.Equal(File.ReadAllBytes(result.File), File.ReadAllBytes(again.Save(root, null).File));
    }

    [Fact]
    public void ConflictingKindAndPolicyLinksDoNotAuthorizeDependentWrites()
    {
        ProfileWith((Attack, 12), (Kind, 1), (Amount, 7), (Link, 2), (Effect, 0xa342));
        byte[] edited = body.ToArray();
        edited[baseline.Cells[Kind].Offset] = 3;
        edited[baseline.Cells[Link].Offset] = 1;
        WriteInput(edited);
        var plan = RuleFileExport.Preview(Input, Profile);
        var states = plan.Edits.ToDictionary(e => e.Key, e => e.Status);
        Assert.Equal(FileEditStatus.Apply, states[Attack]);
        Assert.Equal(FileEditStatus.Conflict, states[Kind]); Assert.Equal(FileEditStatus.Dependency, states[Amount]);
        Assert.Equal(FileEditStatus.Conflict, states[Link]); Assert.Equal(FileEditStatus.Dependency, states[Effect]);
        edited[baseline.Cells[Attack].Offset] = 12;
        Assert.Equal(edited, LwcCodec.Decode(File.ReadAllBytes(plan.Save(root, null).File), RuleFileContainer.Block));
    }

    [Fact]
    public void ReusedRowsTargetEffectAndChangedDependentQuantityAreHeld()
    {
        ProfileWith((Attack, 12), (Defense, 8), (Kind, 1), (Link, 2), (Effect, 200));
        byte[] edited = body.ToArray();
        edited[baseline.Cells[Attack].Offset - 0x73] = 65;
        edited[baseline.Cells[Amount].Offset] = 4;
        // The legacy name prefix can remain byte-identical when the extended effect name changes.
        edited[baseline.Cells[Effect].Offset - 0x84 + 14] = 65;
        WriteInput(edited);
        var states = RuleFileExport.Preview(Input, Profile).Edits.ToDictionary(e => e.Key, e => e.Status);
        Assert.Equal(FileEditStatus.Identity, states[Attack]); Assert.Equal(FileEditStatus.Apply, states[Defense]);
        Assert.Equal(FileEditStatus.Dependency, states[Kind]);
        Assert.Equal(FileEditStatus.Identity, states[Link]); Assert.Equal(FileEditStatus.Identity, states[Effect]);
    }

    [Fact]
    public void KnownFileSchemaAndRowsCanBeExportedFromANewerProfile()
    {
        ProfileWith((Attack, 22));
        var newer = new RuleProfileFile(Profile).Read().Profile! with { Build = new string('B', 64) };
        File.WriteAllBytes(Profile, JsonSerializer.SerializeToUtf8Bytes(newer));
        var plan = RuleFileExport.Preview(Input, Profile);
        Assert.Equal(FileEditStatus.Apply, Assert.Single(plan.Edits).Status);
        var output = plan.Save(root, null);
        Assert.Equal(22, FormationFileData.Read(output.File).Rows[0].Values["attack"]);
    }

    [Fact]
    public void ChosenConflictsRecheckDependentFieldsAndUntouchedBytes()
    {
        ProfileWith((Attack, 22), (Kind, 1), (Amount, 7), (Link, 2), (Effect, 845));
        var edited = (byte[])body.Clone();
        edited[baseline.Cells[Kind].Offset] = 3;
        edited[baseline.Cells[Link].Offset] = 1;
        WriteInput(edited);
        var preview = RuleFileExport.Preview(Input, Profile);
        var chosen = preview.WithConflictChoices(new HashSet<RuleKey> { Kind });
        Assert.Equal(FileEditStatus.Apply, chosen.Edits.Single(e => e.Key == Amount).Status);
        Assert.Equal(FileEditStatus.Dependency, chosen.Edits.Single(e => e.Key == Effect).Status);
        Assert.Throws<InvalidDataException>(() => preview.WithConflictChoices(new HashSet<RuleKey> { Effect }));
        var result = chosen.Save(root, null);
        edited[baseline.Cells[Attack].Offset] = 22;
        edited[baseline.Cells[Kind].Offset] = 1;
        edited[baseline.Cells[Amount].Offset] = 7;
        Assert.Equal(edited, LwcCodec.Decode(File.ReadAllBytes(result.File), RuleFileContainer.Block));
    }

    [Theory]
    [InlineData("formation")]
    [InlineData("policy")]
    public void UnrecognizedTableHoldsOnlyFieldsThatNeedIt(string table)
    {
        ProfileWith((Attack, 18), (Amount, 21), (Link, 2), (Effect, 99));
        var edited = (byte[])body.Clone();
        int start = table == "formation" ? FileRuleLayout.Locate(body, 11, 21, 152, 201)
            : FileRuleLayout.Locate(body, 18, 121, 148, 31);
        edited[start - 8] = 90;
        WriteInput(edited);
        var plan = RuleFileExport.Preview(Input, Profile);
        foreach (var e in plan.Edits)
        {
            bool allowed = table == "formation" ? e.Key != Attack : e.Key == Attack || e.Key == Amount;
            Assert.Equal(allowed, e.Status == FileEditStatus.Apply);
            if (allowed && e.Cell!.Width == 1) edited[e.Cell.Offset] = (byte)e.Desired;
            else if (allowed) BinaryPrimitives.WriteUInt16LittleEndian(edited.AsSpan(e.Cell!.Offset), (ushort)e.Desired);
        }
        Assert.Equal(edited, LwcCodec.Decode(File.ReadAllBytes(plan.Save(root, null).File), RuleFileContainer.Block));
    }

    [Fact]
    public void PreviewCannotOverwriteChangedInputsOrExportToGameDirectory()
    {
        ProfileWith((Attack, 20));
        var plan = RuleFileExport.Preview(Input, Profile);
        string game = Directory.CreateDirectory(Path.Combine(root, "game")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(game, "0010_KO")).FullName;
        Assert.Throws<IOException>(() => plan.Save(child, game));
        ProfileWith((Attack, 21));
        Assert.Throws<IOException>(() => plan.Save(root, null));
        plan = RuleFileExport.Preview(Input, Profile);
        var moved = body.ToArray(); moved[^16] ^= 32; WriteInput(moved);
        Assert.Throws<IOException>(() => plan.Save(root, null));
        Assert.Empty(Directory.GetDirectories(root, "*San14Tenkazu*"));
    }

    [Theory]
    [InlineData("trailing")] [InlineData("hiddenPayload")] [InlineData("checksum")]
    [InlineData("length")] [InlineData("duplicate")] [InlineData("raw")] [InlineData("locale")]
    public void IncompleteAmbiguousOrUnverifiedFilesCannotBeExported(string failure)
    {
        ProfileWith((Attack, 12));
        byte[] file = File.ReadAllBytes(Input);
        switch (failure)
        {
            case "trailing": file = [.. file, 0]; break;
            case "hiddenPayload":
                file = [.. file, 0];
                BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(762), file.Length - 1022); break;
            case "checksum": file[463] ^= 3; break;
            case "length": BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(758), int.MaxValue); break;
            case "duplicate": file = RuleFileContainer.Pack(header, [.. body, .. body]); break;
            case "raw": file = body; break;
            case "locale": file[0x1a6] ^= 1; break;
        }
        File.WriteAllBytes(Input, file);
        Assert.Throws<InvalidDataException>(() => RuleFileExport.Preview(Input, Profile));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
