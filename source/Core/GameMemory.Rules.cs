using System.Security.Cryptography;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal static PlainRuleSnapshot ReadDoctrines(GameIdentity identity) =>
        ReadRuleTable(identity, GameFeature.Doctrines, memory => PlainRuleMemory.Read(memory, identity, DoctrineRules.Spec));

    internal static PlainRuleSnapshot ReadRanks(GameIdentity identity) =>
        ReadRuleTable(identity, GameFeature.Ranks, memory => PlainRuleMemory.Read(memory, identity, RankRules.Spec));

    internal static PlainRuleSnapshot ApplyPlainRules(GameIdentity identity, PlainRuleSnapshot expected, IReadOnlyList<RuleChange> changes) =>
        WriteRuleTable(identity, expected.Spec.Table switch
        {
            "doctrine" => GameFeature.Doctrines,
            "rank" => GameFeature.Ranks,
            _ => throw new InvalidDataException(L.Rules.Incompatible)
        }, memory => PlainRuleMemory.Apply(memory, identity, expected, changes));

    internal static OfficeSnapshot ReadOffices(GameIdentity identity) =>
        ReadRuleTable(identity, GameFeature.Offices, memory => OfficeMemory.Read(memory, identity));

    internal static OfficeSnapshot ApplyOffices(GameIdentity identity, OfficeSnapshot expected, IReadOnlyList<RuleChange> changes) =>
        WriteRuleTable(identity, GameFeature.Offices, memory => OfficeMemory.Apply(memory, identity, expected, changes));

    internal static PolicySnapshot ReadPolicies(GameIdentity identity) =>
        ReadRuleTable(identity, GameFeature.Policies, memory => PolicyMemory.Read(memory, identity));

    internal static PolicySnapshot ApplyPolicies(GameIdentity identity, PolicySnapshot expected, IReadOnlyList<RuleChange> changes) =>
        WriteRuleTable(identity, GameFeature.Policies, memory => PolicyMemory.Apply(memory, identity, expected, changes));

    // Re-resolve only the profiled root. During a load, unavailable tables wait for the next refresh;
    // do not start a whole-process fallback scan over objects being destroyed/reconstructed.
    internal static GameLayout ReadCurrentLayout(GameIdentity identity)
    {
        var session = RequirePlayingSession(identity);
        if (TryReadCurrentLayout(identity) is { } layout) return layout;
        if (identity.DataProfile is not null)
            throw new GameSessionUnavailableException(session with { Phase = GameSessionPhase.Transition, Error = "World tables are not ready" });
        throw new InvalidOperationException(L.Rules.NotReady);
    }

    internal static GameLayout? TryReadCurrentLayout(GameIdentity identity)
    {
        var readOnly = identity.DataProfile;
        if ((!identity.Verified && readOnly is null) || !IsSameGame(identity))
            throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            ulong worldPointerRva = readOnly?.WorldPointerRva ?? BasePointerRva;
            ulong root = ReadPointers(handle, identity.ModuleBase + worldPointerRva, 1)[0];
            return root == 0 ? null : TryBuildLayout(handle, identity, ProfiledTables(checked(root + BaseAdjust)));
        }
        finally { CloseHandle(handle); }
    }

    private static void RequireSnapshotRoot(IntPtr handle, GameIdentity identity, GameLayout layout, GameDataProfile profile,
        GameSessionObservation session)
    {
        ulong root = ReadPointers(handle, identity.ModuleBase + profile.WorldPointerRva, 1)[0];
        var tables = layout.Tables;
        bool SameArray(ulong table, ulong array) => array == 0 || ReadPointers(handle, table, 1)[0] == array;
        if (root == 0 || ProfiledTables(checked(root + BaseAdjust)) != tables ||
            !SameArray(tables.City, layout.CityArray) || !SameArray(tables.County, layout.CountyArray) ||
            !SameArray(tables.Officer, layout.OfficerArray) || !SameArray(tables.Tile, layout.TileArray) ||
            !SameArray(tables.Building, layout.BuildingArray) || !SameArray(tables.Unit, layout.UnitArray) ||
            !SameArray(tables.Item, layout.ItemArray) || !SameArray(tables.State, layout.State) ||
            !ReadPointers(handle, tables.Force, ForceCount).AsSpan().SequenceEqual(layout.Forces) ||
            !ReadPointers(handle, tables.Corps, ForceCount).AsSpan().SequenceEqual(layout.Corps))
            throw new GameSessionUnavailableException(session with { Phase = GameSessionPhase.Transition, Error = "World storage changed during snapshot read" });
    }

    internal static FormationSnapshot ReadFormations(GameIdentity identity) =>
        ReadRuleTable(identity, GameFeature.RuleTables, memory => FormationMemory.Read(memory, identity));

    internal static FormationSnapshot ApplyFormations(GameIdentity identity, FormationSnapshot expected, IReadOnlyList<RuleChange> changes) =>
        WriteRuleTable(identity, GameFeature.RuleTables, memory => FormationMemory.Apply(memory, identity, expected, changes));

    internal static TacticSnapshot ReadTactics(GameIdentity identity) =>
        ReadRuleTable(identity, GameFeature.Tactics, memory => TacticMemory.Read(memory, identity));

    internal static TacticSnapshot ApplyTactics(GameIdentity identity, TacticSnapshot expected, IReadOnlyList<RuleChange> changes) =>
        WriteRuleTable(identity, GameFeature.Tactics, memory => TacticMemory.Apply(memory, identity, expected, changes));

    private static T ReadRuleTable<T>(GameIdentity identity, GameFeature feature, Func<IRecordMemory, T> read)
    {
        GameCapabilities.Require(identity, feature);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            if (identity.DataProfile is not { } profile) return read(new ProcessMemory(handle));
            var session = RequirePlayingSession(identity);
            RequireRuleCode(handle, identity, profile);
            T result = read(new ProcessMemory(handle));
            if (session.Entries[2].Address != RequirePlayingSession(identity).Entries[2].Address)
                throw new InvalidOperationException(L.Rules.Stale);
            return result;
        }
        finally { CloseHandle(handle); }
    }

    private static T WriteRuleTable<T>(GameIdentity identity, GameFeature feature, Func<IRecordMemory, T> write)
    {
        GameCapabilities.Require(identity, feature);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        if (identity.DataProfile is { } profile)
            return WithProfiledRuleTable(identity, ReadCurrentLayout(identity), profile, (memory, _) => write(memory));
        IntPtr legacy = OpenGameWrite(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, identity);
        if (legacy == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try { return write(new ProcessMemory(legacy)); }
        finally { CloseHandle(legacy); }
    }

    private static T WithProfiledRuleTable<T>(GameIdentity identity, GameLayout layout, GameDataProfile profile,
        Func<IRecordMemory, GameSnapshot, T> action) =>
        WithProfiledEditSession(identity, layout, (handle, data) =>
        {
            RequireRuleCode(handle, identity, profile);
            return action(new ProfiledRuleMemory(handle), data);
        });

    private static void RequireRuleCode(IntPtr handle, GameIdentity identity, GameDataProfile profile)
    {
        if (profile.RuleGuards.IsDefaultOrEmpty) throw new InvalidDataException(L.Rules.Incompatible);
        foreach (var guard in profile.RuleGuards)
        {
            ulong address = checked(identity.ModuleBase + guard.Rva);
            RequireEditPage(handle, address, guard.Length, 0x20, identity.ModuleBase);
            if (Convert.ToHexString(SHA256.HashData(ReadBytes(handle, address, guard.Length))) != guard.Sha256)
                throw new InvalidDataException(L.Connection.EditCodeChanged);
        }
    }

    private sealed class ProfiledRuleMemory(IntPtr handle) : IRecordMemory
    {
        public byte[] Read(ulong address, int length) => ReadBytes(handle, address, length);
        public void Write(ulong address, byte[] data)
        {
            RequireEditPage(handle, address, data.Length, 0x04);
            WriteBytes(handle, address, data);
        }
    }
}
