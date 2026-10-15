#if LOCAL_DEVELOPER
using System.Security.Cryptography;
using System.Text.Json;
using San14Tenkazu.EnvironmentRules;
using San14Tenkazu.Seasons;

namespace San14Tenkazu;

internal sealed record CompatibilityEngineRead(ulong Rva, int Size, string ExpectedSha256, string ActualSha256,
    string ActualHex, uint State, uint Protection, bool Original, bool ExecutableReadOnly);
internal sealed record CompatibilityTableRead(GameFeature Feature, bool Passed, string Evidence);
internal sealed record CompatibilityRead(GameIdentity Identity, GameLayout Layout, string SnapshotSha256, string CatalogSha256,
    string? Date, int Hour, GamePace Pace, bool GrowthKnown, CompatibilityEngineRead[] Engine,
    CompatibilityTableRead[] Tables, GameChecks.Check[] Checks);
internal sealed record CompatibilityReport(bool Consistent, string? Error, CompatibilityRead? First, CompatibilityRead? Second,
    GameCapability[] Capabilities);

internal static class CompatibilityDiagnostic
{
    // The seam deliberately accepts no writer, injector, controller, or remote-call dependency.
    internal static CompatibilityReport Read(Func<CompatibilityRead> capture, Func<bool> connectionCurrent, CancellationToken token)
    {
        CompatibilityRead? first = null, second = null;
        try
        {
            token.ThrowIfCancellationRequested();
            first = capture(); token.ThrowIfCancellationRequested();
            if (first.Pace != GamePace.Paused) throw new InvalidOperationException("게임을 일시정지한 뒤 실행하세요.");
            second = capture(); token.ThrowIfCancellationRequested();
            if (!connectionCurrent() || first.Identity != second.Identity || !first.Layout.SameStorage(second.Layout)
                || second.Pace != GamePace.Paused || first.Date != second.Date || first.Hour != second.Hour
                || first.SnapshotSha256 != second.SnapshotSha256 || first.CatalogSha256 != second.CatalogSha256
                || first.GrowthKnown != second.GrowthKnown || !first.Engine.SequenceEqual(second.Engine)
                || !first.Tables.SequenceEqual(second.Tables) || !first.Checks.SequenceEqual(second.Checks))
                throw new InvalidOperationException("두 번 읽는 동안 연결·저장소·값·코드·권한이 바뀌었습니다. 미확정 결과입니다.");
            var capabilities = GameCapabilities.Inspect(first.Identity).Select(c =>
            {
                var evidence = new List<CapabilityEvidence> { new("storage", true, "two stable read-only captures") };
                if (c.Feature == GameFeature.AbilityData) evidence.Add(new("growth-curve", first.GrowthKnown, "recognized runtime growth curve"));
                if (c.Feature is GameFeature.Seasons or GameFeature.Environment)
                {
                    var targets = c.Feature == GameFeature.Seasons ? GameBuildProfiles.Current.SeasonTargets : GameBuildProfiles.Current.EnvironmentTargets;
                    var engine = first.Engine.Where(e => targets.Any(t => t.Rva == e.Rva && t.Size == e.Size)).ToArray();
                    evidence.Add(new("engine-code", engine.Length == targets.Length && engine.All(e => e.Original), "exact original bytes"));
                    evidence.Add(new("engine-protection", engine.Length == targets.Length && engine.All(e => e.ExecutableReadOnly), "MEM_COMMIT/PAGE_EXECUTE_READ"));
                    // No native session is started: eligibility must never become a native execution certificate.
                }
                if (first.Tables.SingleOrDefault(t => t.Feature == c.Feature) is {} table)
                    evidence.Add(new(c.Feature is GameFeature.Seasons or GameFeature.Environment ? "bundle" : "operation-guards", table.Passed, table.Evidence));
                return GameCapabilities.Record(c, [.. evidence]);
            }).ToArray();
            return new(true, null, first, second, capabilities);
        }
        catch (Exception error)
        {
            return new(false, error is OperationCanceledException ? "취소되었습니다. 미확정 결과입니다." : error.Message,
                first, second, []);
        }
    }
}

internal static partial class GameMemory
{
    internal static CompatibilityReport ReadCompatibilityDiagnostic(GameIdentity identity, Func<bool> connectionCurrent, CancellationToken token)
        => CompatibilityDiagnostic.Read(() => CaptureCompatibility(identity, token), () => IsSameGame(identity) && connectionCurrent(), token);

    private static CompatibilityRead CaptureCompatibility(GameIdentity identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (FindGame() != identity) throw new InvalidOperationException(L.Errors.ProcessChanged);
        var found = identity.Verified ? ReadCurrentLayout(identity) : Resolve(identity, null, token);
        if (ReadPace(identity, found) != GamePace.Paused) throw new InvalidOperationException("게임을 일시정지한 뒤 실행하세요.");
        var data = ReadSnapshot(identity, found); var catalog = ReadCatalog(identity, found);
        string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
        string JsonHash(object value) => Hash(JsonSerializer.SerializeToUtf8Bytes(value));
        var snapshotHash = JsonHash(new {
            cities = data.Cities.Select(r => new { r.Address, r.Data }), counties = data.Counties.Select(r => new { r.Address, r.Data }),
            officers = data.Officers.Select(r => new { r.Address, r.Data }), castles = data.CastleByCity.OrderBy(p => p.Key).Select(p => new { p.Key, p.Value.Address, p.Value.Data }),
            forces = data.ForceRecords.OrderBy(p => p.Key).Select(p => new { p.Key, p.Value.Address, p.Value.Data }),
            corps = data.CorpsRecords.OrderBy(p => p.Key).Select(p => new { p.Key, p.Value.Address, p.Value.Data }),
            units = data.Units.Select(r => new { r.Address, r.Data }), items = data.Items.Select(r => new { r.Address, r.Data }),
            traits = data.Personalities.Select(r => new { r.Address, r.Data }), tribes = data.Tribes.Select(r => new { r.Address, r.Data }),
            gates = data.Gates.Select(r => new { r.Address, r.Data }), data.TileOwners, data.TileCounties, data.CorpsForce, data.CorpsLeaders,
            play = data.Play?.Data, data.PlayerForce, data.DemoFlag, data.RuntimeRules });
        var tables = new List<CompatibilityTableRead>();
        void Table(GameFeature feature, Func<string> read)
        {
            token.ThrowIfCancellationRequested();
            try { GameCapabilities.Require(identity, feature); tables.Add(new(feature, true, read())); }
            catch (Exception error) { tables.Add(new(feature, false, error.Message)); }
        }
        Table(GameFeature.RuleTables, () => { var s=ReadFormations(identity); return JsonHash(new {s.Root,s.State,s.Array,s.Catalog,values=s.Values.OrderBy(v=>v.Key.Record).ThenBy(v=>v.Key.Field)}); });
        Table(GameFeature.Tactics, () => { var s=ReadTactics(identity); return JsonHash(new {s.Root,s.State,s.Array,s.Catalog,values=s.Values.OrderBy(v=>v.Key.Record).ThenBy(v=>v.Key.Field)}); });
        Table(GameFeature.Policies, () => { var s=ReadPolicies(identity); return JsonHash(new {s.Root,s.State,s.Catalogs,values=s.Values.OrderBy(v=>v.Key.Table).ThenBy(v=>v.Key.Record).ThenBy(v=>v.Key.Field)}); });
        Table(GameFeature.Offices, () => { var s=ReadOffices(identity); return JsonHash(new {s.Root,s.State,s.Array,s.Catalog,values=s.Values.OrderBy(v=>v.Key.Record).ThenBy(v=>v.Key.Field)}); });
        Table(GameFeature.Ranks, () => { var s=ReadRanks(identity); return JsonHash(new {s.Root,s.State,s.Array,s.Catalog,values=s.Values.OrderBy(v=>v.Key.Record).ThenBy(v=>v.Key.Field)}); });
        Table(GameFeature.Doctrines, () => { var s=ReadDoctrines(identity); return JsonHash(new {s.Root,s.State,s.Array,s.Catalog,values=s.Values.OrderBy(v=>v.Key.Record).ThenBy(v=>v.Key.Field)}); });
        Table(GameFeature.Seasons, () => { SeasonNativeBundle.VerifyEmbedded(); return SeasonNativeBundle.DllHash; });
        Table(GameFeature.Environment, () => { EnvironmentNativeBundle.VerifyEmbedded(); return EnvironmentNativeBundle.DllHash; });
        var engine = new List<CompatibilityEngineRead>();
        if (identity.Verified && GameBuildProfiles.Find(identity.ExeHash) is {} profile)
        {
            IntPtr handle=OpenRead(identity.ProcessId);
            try
            {
                foreach (var t in profile.SeasonTargets.Concat(profile.EnvironmentTargets).Distinct())
                {
                    token.ThrowIfCancellationRequested(); ulong address=checked(identity.ModuleBase+t.Rva);
                    byte[] bytes=ReadBytes(handle,address,t.Size), info=new byte[48];
                    if (VirtualQueryEx(handle,(IntPtr)address,info,(UIntPtr)info.Length).ToUInt64() != (ulong)info.Length) throw new IOException("Native page query incomplete");
                    ulong region=BitConverter.ToUInt64(info,0),size=BitConverter.ToUInt64(info,24);
                    uint state=BitConverter.ToUInt32(info,32),protection=BitConverter.ToUInt32(info,36);
                    string hash=Hash(bytes);
                    engine.Add(new(t.Rva,t.Size,t.Hash,hash,Convert.ToHexString(bytes),state,protection,hash.Equals(t.Hash,StringComparison.OrdinalIgnoreCase),
                        region<=address&&size>=(ulong)t.Size&&address-region<=size-(ulong)t.Size&&state==0x1000&&protection==0x20));
                }
            }
            finally { CloseHandle(handle); }
        }
        token.ThrowIfCancellationRequested();
        var last=identity.Verified ? ReadCurrentLayout(identity) : Resolve(identity,null,token);
        if (!found.SameStorage(last) || !IsSameGame(identity)) throw new InvalidOperationException(L.Errors.ProcessChanged);
        return new(identity,found,snapshotHash,JsonHash(catalog),data.Date is {} d?$"{d.Year}/{d.Month}/{d.Day}":null,data.Hour,
            ReadPace(identity,last),data.RuntimeRules is not null,[..engine],[..tables],[..GameChecks.Run(data,catalog)]);
    }
}
#endif
