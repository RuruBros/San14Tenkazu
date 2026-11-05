using System.Security.Cryptography;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    private static readonly GameDataGuard[] NovemberAiCode =
    [
        new(0x1F100, 64, "DDD77E71D587A4DEC12F984D1A2FE84D715F23628A4C3A6A20FE535C3F794616"),
        new(0xAF520, 420, "B331F2FB55A5850FD6B5DB20509D05D9AF633D2B5C1A6281980DD0E2F63BEEF9")
    ];

    // The new manager is tied to the exact executable and stock tree. Keep the legacy tree path independent.
    internal static AiLiveTree[] ReadAiTrees(GameIdentity identity, AiTree[] file, string? stamp = null)
    {
        if (!AiBuildProfile.IsNovember(identity))
            return ReadAi(identity, memory => AiMemory.Read(memory, identity.ModuleBase, file));
        if (stamp != AiBuildProfile.NovemberStock) throw new InvalidDataException(L.Ai.TreeChanged);
        RequirePlayingSession(identity);
        GameCapabilities.Require(identity, GameFeature.AiTrees);
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            RequireNovemberAiCode(handle, identity);
            return AiMemory.Read(new ProcessMemory(handle), identity.ModuleBase, file, AiBuildProfile.NovemberManager);
        }
        finally { CloseHandle(handle); }
    }

    // A record of an AI write as the game holds it now, for [되돌리기] (the snapshot has no AI nodes); null when it
    // cannot be read.
    internal static byte[]? ReadAiRecord(GameIdentity identity, RecordEdit edit)
    {
        try { return ReadAi(identity, memory => memory.Read(edit.Address, edit.Expected.Length)); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    // Something read from the game's memory through IRecordMemory (the AI's numbers: AiMemory.Value, Differences).
    internal static T ReadAi<T>(GameIdentity identity, Func<IRecordMemory, T> read)
    {
        if (!identity.Verified && !AiBuildProfile.IsNovember(identity)) throw new InvalidOperationException(L.Ai.Unverified);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        GameCapabilities.Require(identity, GameFeature.AiTrees);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return read(new ProcessMemory(handle)); }
        finally { CloseHandle(handle); }
    }

    private static void RequireNovemberAiCode(IntPtr handle, GameIdentity identity)
    {
        foreach (var guard in NovemberAiCode)
        {
            ulong address = checked(identity.ModuleBase + guard.Rva);
            RequireEditPage(handle, address, guard.Length, 0x20, identity.ModuleBase);
            if (Convert.ToHexString(SHA256.HashData(ReadBytes(handle, address, guard.Length))) != guard.Sha256)
                throw new InvalidDataException(L.Connection.EditCodeChanged);
        }
    }

    private static IReadOnlyList<string> ApplyProfiledAiEdits(GameIdentity identity, GameLayout? layout, IReadOnlyList<RecordEdit> edits)
    {
        if (layout is null) throw new InvalidOperationException(L.Errors.StateMoved);
        var profile = LoadProfiledAi(identity);
        return WithProfiledEditSession(identity, layout, (handle, data) =>
            ApplyProfiledAiTransaction(handle, identity, layout, data, profile, edits));
    }

    private sealed record ProfiledAi(AiTree[] Trees, AiGuideValue[] Values, IReadOnlyDictionary<AiSlot, int> Allowed);

    private static ProfiledAi LoadProfiledAi(GameIdentity identity)
    {
        if (!AiBuildProfile.IsNovember(identity)) throw new InvalidOperationException(L.Ai.Unverified);
        GameCapabilities.Require(identity, GameFeature.AiTrees);
        string executable = ExecutablePath(identity) ?? throw new InvalidOperationException(L.Errors.ProcessChanged);
        byte[] stock = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(executable)!, AiTreeFile.RelativePath));
        string stamp = AiTreeFile.Stamp(stock);
        if (stamp != AiBuildProfile.NovemberStock) throw new InvalidDataException(L.Ai.TreeChanged);
        AiTree[] trees = AiTreeFile.Parse(stock);
        var values = AiBuildProfile.Values(AiBuildProfile.Pages(stamp, trees)).ToArray();
        var allowed = new Dictionary<AiSlot, int>();
        foreach (var value in values)
        {
            if (AiEdits.FileRaw(trees, value) is not int original) throw new InvalidDataException(L.Ai.TreeChanged);
            foreach (var slot in AiEdits.Slots(trees, value))
            {
                int maximum = AiEdits.MaxRaw(original);
                if (!allowed.TryAdd(slot, maximum) && allowed[slot] != maximum)
                    throw new InvalidDataException(L.Ai.TreeChanged);
            }
        }
        return new(trees, values, allowed);
    }

    private static IReadOnlyList<string> ApplyProfiledAiTransaction(IntPtr handle, GameIdentity identity, GameLayout layout,
        GameSnapshot data, ProfiledAi profile, IReadOnlyList<RecordEdit> edits)
    {
        var dataProfile = identity.DataProfile ?? throw new InvalidOperationException(L.Ai.Unverified);
        RequireNovemberAiCode(handle, identity);
        var memory = new ProcessMemory(handle);
        RequireProfiledAiTargets(memory, identity.ModuleBase, profile.Trees, profile.Allowed, edits);
        foreach (var edit in edits) RequireEditPage(handle, edit.Address, edit.Expected.Length, 0x04);
        return Apply(memory, identity.Classes, edits, () =>
        {
            var after = RequirePlayingSession(identity);
            if (data.Session?.Entries[2].Address != after.Entries[2].Address)
                throw new InvalidOperationException(L.Errors.StateMoved);
            RequireSnapshotRoot(handle, identity, layout, dataProfile, after);
        });
    }

    internal static void RequireProfiledAiTargets(IRecordMemory memory, ulong module, AiTree[] trees,
        IReadOnlyDictionary<AiSlot, int> allowed, IReadOnlyList<RecordEdit> edits)
    {
        var cells = new HashSet<ulong>();
        foreach (var edit in edits)
        {
            if (edit.AiSlots is not { } slots || edit.Changes.Count == 0 || edit.Restarts is { Count: > 0 } ||
                edit.MilitaryPoints is not null || edit.ExpectedRuntimeRules is not null)
                throw new InvalidDataException(L.Ai.TreeChanged);
            var current = memory.Read(edit.Address, edit.Expected.Length);
            if (!IsSameRecord(edit.Kind, current, edit.Expected)) throw new InvalidDataException(L.Ai.TreeChanged);
            foreach (var change in edit.Changes)
            {
                var field = change.Field;
                if (field.Kind != FieldKind.Int32 || field.Record != edit.Kind || !slots.TryGetValue(field.Offset, out var slot) ||
                    !allowed.TryGetValue(slot, out int maximum) || change.Value < 0 || change.Value > maximum)
                    throw new InvalidDataException(L.Ai.TreeChanged);
                AiTree tree = trees[(int)slot.Tree];
                ulong address = AiMemory.ReadNode(memory, module, AiBuildProfile.NovemberManager, tree, slot.Node);
                var actual = AiMemory.Locate(memory, tree.Nodes[slot.Node], address, slot.Param);
                if (actual != (edit.Kind, edit.Address, edit.Expected.Length, field.Offset) ||
                    !cells.Add(checked(edit.Address + (ulong)field.Offset)))
                    throw new InvalidDataException(L.Ai.TreeChanged);
                if (field.Read(current) != field.Read(edit.Expected)) throw new StaleEditException(edit.Label, [field.Label]);
            }
        }
    }
}
