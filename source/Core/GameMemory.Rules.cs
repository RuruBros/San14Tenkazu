namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal static PlainRuleSnapshot ReadDoctrines(GameIdentity identity)
    {
        GameCapabilities.Require(identity, GameFeature.Doctrines);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle=OpenRead(identity.ProcessId);
        try { return PlainRuleMemory.Read(new ProcessMemory(handle),identity,DoctrineRules.Spec); } finally { CloseHandle(handle); }
    }
    internal static PlainRuleSnapshot ReadRanks(GameIdentity identity)
    {
        GameCapabilities.Require(identity, GameFeature.Ranks);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle=OpenRead(identity.ProcessId);
        try { return PlainRuleMemory.Read(new ProcessMemory(handle),identity,RankRules.Spec); } finally { CloseHandle(handle); }
    }
    internal static PlainRuleSnapshot ApplyPlainRules(GameIdentity identity,PlainRuleSnapshot expected,IReadOnlyList<RuleChange> changes)
    {
        GameCapabilities.Require(identity, expected.Spec.Table switch { "doctrine" => GameFeature.Doctrines, "rank" => GameFeature.Ranks, _ => throw new InvalidDataException(L.Rules.Incompatible) });
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        IntPtr handle=OpenProcess(ProcessQueryInformation|ProcessVmRead|ProcessVmWrite|ProcessVmOperation,false,identity.ProcessId);
        if (handle==IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try { return PlainRuleMemory.Apply(new ProcessMemory(handle),identity,expected,changes); } finally { CloseHandle(handle); }
    }
    internal static OfficeSnapshot ReadOffices(GameIdentity identity)
    {
        GameCapabilities.Require(identity, GameFeature.Offices);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return OfficeMemory.Read(new ProcessMemory(handle), identity); }
        finally { CloseHandle(handle); }
    }
    internal static OfficeSnapshot ApplyOffices(GameIdentity identity, OfficeSnapshot expected, IReadOnlyList<RuleChange> changes)
    {
        GameCapabilities.Require(identity, GameFeature.Offices);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try { return OfficeMemory.Apply(new ProcessMemory(handle), identity, expected, changes); }
        finally { CloseHandle(handle); }
    }
    internal static PolicySnapshot ReadPolicies(GameIdentity identity)
    {
        GameCapabilities.Require(identity, GameFeature.Policies);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return PolicyMemory.Read(new ProcessMemory(handle), identity); }
        finally { CloseHandle(handle); }
    }
    internal static PolicySnapshot ApplyPolicies(GameIdentity identity, PolicySnapshot expected, IReadOnlyList<RuleChange> changes)
    {
        GameCapabilities.Require(identity, GameFeature.Policies);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try { return PolicyMemory.Apply(new ProcessMemory(handle), identity, expected, changes); }
        finally { CloseHandle(handle); }
    }
    // Re-resolve only the verified root. During a load, unavailable tables wait for the next refresh;
    // do not start a whole-process fallback scan over objects being destroyed/reconstructed.
    internal static GameLayout ReadCurrentLayout(GameIdentity identity)
    {
        RequirePlayingSession(identity);
        return TryReadCurrentLayout(identity) ?? throw new InvalidOperationException(L.Rules.NotReady);
    }

    internal static GameLayout? TryReadCurrentLayout(GameIdentity identity)
    {
        if (!identity.Verified || !IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try
        {
            ulong root = ReadPointers(handle, identity.ModuleBase + BasePointerRva, 1)[0];
            return root == 0 ? null : TryBuildLayout(handle, identity, VerifiedTables(root + BaseAdjust));
        }
        finally { CloseHandle(handle); }
    }

    internal static FormationSnapshot ReadFormations(GameIdentity identity)
    {
        GameCapabilities.Require(identity, GameFeature.RuleTables);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return FormationMemory.Read(new ProcessMemory(handle), identity); }
        finally { CloseHandle(handle); }
    }

    internal static FormationSnapshot ApplyFormations(GameIdentity identity, FormationSnapshot expected, IReadOnlyList<RuleChange> changes)
    {
        GameCapabilities.Require(identity, GameFeature.RuleTables);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try { return FormationMemory.Apply(new ProcessMemory(handle), identity, expected, changes); }
        finally { CloseHandle(handle); }
    }

    internal static TacticSnapshot ReadTactics(GameIdentity identity)
    {
        GameCapabilities.Require(identity, GameFeature.Tactics);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        IntPtr handle = OpenRead(identity.ProcessId);
        try { return TacticMemory.Read(new ProcessMemory(handle), identity); }
        finally { CloseHandle(handle); }
    }

    internal static TacticSnapshot ApplyTactics(GameIdentity identity, TacticSnapshot expected, IReadOnlyList<RuleChange> changes)
    {
        GameCapabilities.Require(identity, GameFeature.Tactics);
        if (!IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChangedReconnect);
        IntPtr handle = OpenProcess(ProcessQueryInformation | ProcessVmRead | ProcessVmWrite | ProcessVmOperation, false, identity.ProcessId);
        if (handle == IntPtr.Zero) throw AccessError(L.Errors.NoWriteAccess);
        try { return TacticMemory.Apply(new ProcessMemory(handle), identity, expected, changes); }
        finally { CloseHandle(handle); }
    }
}
