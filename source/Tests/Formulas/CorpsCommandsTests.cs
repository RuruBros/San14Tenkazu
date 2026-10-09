using System.Text.Json;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

public class CorpsCommandsTests
{
    private static ulong Address(string s) => Convert.ToUInt64(s[2..], 16);

    [Theory]
    [InlineData("corps_commands.json.gz")]
    [InlineData("corps_fall.json.gz")]
    [InlineData("officer_corps.json.gz")]
    [InlineData("marked_officer.json.gz")]
    [InlineData("marked_capture.json.gz")]
    [InlineData("free_officer.json.gz")]
    [InlineData("free_destination.json.gz")]
    [InlineData("free_consumer.json.gz")]
    [InlineData("marked_free_capture.json.gz")]
    [InlineData("prisoner_release.json.gz")]
    [InlineData("return_start.json.gz")]
    [InlineData("unit_destruction.json.gz")]
    [InlineData("officer_death.json.gz")]
    [InlineData("death_inheritance.json.gz")]
    [InlineData("officer_death_extended.json.gz")]
    [InlineData("force_relationships.json.gz")]
    [InlineData("officer_death_attacker.json.gz")]
    [InlineData("officer_death_corps.json.gz")]
    [InlineData("officer_death_corps_unit.json.gz")]
    public void CorpsTransitionsMatchCompleteNativeBodies(string filename)
    {
        var root = Vectors.Read(filename);
        var snapshot = Vectors.Read(root.GetProperty("world").GetString()!);
        Assert.Empty(root.GetProperty("gameplay_stubs").EnumerateArray());
        int totalAllocations = 0;
        foreach (var c in root.GetProperty("cases").EnumerateArray())
        {
            World w = Vectors.LoadWorld(root.GetProperty("world").GetString()!);
            var f = c.GetProperty("fixture");
            string name = f.GetProperty("name").GetString()!;
            Rec? Ref(JsonElement r) => r.ValueKind == JsonValueKind.Null ? null :
                w.Table(Convert.ToInt32(r[0].GetString()![2..], 16))[r[1].GetInt32()];
            var pointers = snapshot.GetProperty("tables").EnumerateObject()
                .SelectMany(t => w.Table(Convert.ToInt32(t.Name[2..], 16))).ToDictionary(r => r.NativeAddress);
            var cache = snapshot.GetProperty("corps_cache").EnumerateObject()
                .ToDictionary(p => Address(p.Name), p => Address(p.Value.GetString()!));
            var members = snapshot.GetProperty("nested").EnumerateObject().Where(p => p.Name.StartsWith("caution:"))
                .ToDictionary(p => int.Parse(p.Name[8..]), p => p.Value.EnumerateArray().Select(e => (Rec?)pointers[Address(e.GetString()!)]).ToList());
            var deferred = snapshot.GetProperty("nested").EnumerateObject().Where(p => p.Name.StartsWith("force_deferred:"))
                .ToDictionary(p => int.Parse(p.Name[15..]), p => p.Value.EnumerateArray().Select(e => (Rec?)pointers[Address(e.GetString()!)]).ToList());
            var free = snapshot.GetProperty("nested").TryGetProperty("free_officers", out var pendingFree)
                ? pendingFree.EnumerateArray().Select(e => (Rec?)pointers[Address(e.GetString()!)]).ToList() : new List<Rec?>();
            Vectors.Poke(w, f.GetProperty("pokes"));
            if (f.TryGetProperty("duplicates", out var duplicates))
                foreach (var item in duplicates.EnumerateArray())
                    w.List(Lists.OwnedItems).Add([w.Table(Tables.Items)[item.GetInt32()]]);
            if (f.TryGetProperty("pending_units", out var pendingUnits))
                foreach (var unit in pendingUnits.EnumerateArray())
                {
                    Rec target = w.Table(Tables.Units)[unit.GetInt32()];
                    if (!w.List(Lists.EngagedUnits).Any(n => n[0] == target)) w.List(Lists.EngagedUnits).Add([target]);
                }
            var initialLists = snapshot.GetProperty("lists").EnumerateObject().ToDictionary(p => p.Name,
                p => w.List(Convert.ToInt32(p.Name[2..], 16)).Select(n => n.ToArray()).ToArray());
            var records = snapshot.GetProperty("tables").EnumerateObject().ToDictionary(t => t.Name,
                t => w.Table(Convert.ToInt32(t.Name[2..], 16)).Select(r => r.Bytes.ToArray()).ToArray());
            byte[] worldBefore = w.WorldState.Bytes.ToArray(), settingsBefore = w.Settings.Bytes.ToArray();
            var commands = ReturnCommandTests.Commands(w, snapshot);
            var detached = new List<ulong>();
            var disposed = new List<ulong>();
            int allocations = 0, refreshes = 0;
            var storage = ReturnCommandTests.Storage(w, snapshot, commands, command => detached.Add(command.Data.NativeAddress),
                command => disposed.Add(command.Data.NativeAddress));
            Rec Allocate()
            {
                var input = c.GetProperty("allocations")[allocations++];
                return new(-1, Convert.FromHexString(input.GetProperty("data").GetString()!),
                    nativeAddress: Address(input.GetProperty("address").GetString()!));
            }
            var lifecycle = new CorpsLifecycle(w, cache, members, () => true, storage, Allocate, _ => refreshes++);
            var officerLifecycle = new OfficerLifecycle(w, lifecycle, storage, deferred, free);
            var prisonerRelease = new PrisonerRelease(w, officerLifecycle, storage, Allocate, _ => refreshes++);
            var unitRefreshes = new List<Rec>();
            var moraleVisuals = new List<(int Unit, int Effect)>();
            var difficultyLogs = new List<UnitDestruction.DifficultyLog>();
            var propagation = snapshot.GetProperty("nested").EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.EnumerateArray()
                    .Select(e => Address(e.GetString()!) is var a && a != 0 ? pointers[a] : (Rec?)null).ToList());
            bool refreshAvailable = snapshot.TryGetProperty("personality_refresh_available", out var refreshHost) && refreshHost.GetBoolean();
            var unitPersonalities = new UnitPersonalityLifecycle(w, propagation, refreshAvailable, unitRefreshes.Add);
            var destruction = new UnitDestruction(w, unitPersonalities, prisonerRelease, officerLifecycle,
                refreshAvailable, unitRefreshes.Add, (u, v) => moraleVisuals.Add((u.Index, v)), difficultyLogs.Add,
                _ => throw new NotSupportedException("This vector must record a recursive route before it can be compared."));
            var death = new OfficerDeath(w, officerLifecycle, storage, destruction);
            var rng = new Rng(snapshot.TryGetProperty("initial_rng", out var initialRng)
                ? BitConverter.ToInt32(Convert.FromHexString(initialRng.GetString()!)) : 0);
            var inheritance = new DeathInheritance(w, rng);
            Rec? source = f.GetProperty("source").ValueKind == JsonValueKind.Null ? null : w.Table(Tables.Corps)[f.GetProperty("source").GetInt32()];
            if (filename == "free_consumer.json.gz")
                foreach (var person in f.GetProperty("queue").EnumerateArray()) officerLifecycle.QueueFree(Ref(person)!);
            int result = filename switch
            {
                "free_consumer.json.gz" => officerLifecycle.ConsumeFree(f.GetProperty("count").GetInt32()),
                "prisoner_release.json.gz" => prisonerRelease.Apply(Ref(f.GetProperty("person"))),
                "return_start.json.gz" => prisonerRelease.StartReturn(Ref(f.GetProperty("person"))!),
                "unit_destruction.json.gz" => Destroy(),
                "officer_death.json.gz" => death.ApplyOrdinaryWithoutItems(Ref(f.GetProperty("person"))!, f.GetProperty("destroy_own_unit").GetInt32() != 0),
                "officer_death_extended.json.gz" => death.ApplyOrdinary(Ref(f.GetProperty("person"))!, f.GetProperty("destroy_own_unit").GetInt32() != 0, inheritance),
                "officer_death_attacker.json.gz" => death.ApplyOrdinary(Ref(f.GetProperty("person"))!, f.GetProperty("destroy_own_unit").GetInt32() != 0, inheritance, Ref(f.GetProperty("attacker"))),
                "officer_death_corps.json.gz" => death.ApplyCorpsLeader(Ref(f.GetProperty("person"))!, inheritance, lifecycle, Ref(f.GetProperty("attacker"))),
                "officer_death_corps_unit.json.gz" => death.ApplyCorpsLeader(Ref(f.GetProperty("person"))!, inheritance, lifecycle,
                    Ref(f.GetProperty("attacker")), f.GetProperty("destroy_own_unit").GetInt32() != 0),
                "death_inheritance.json.gz" => Inherit(),
                "force_relationships.json.gz" => ForceRelations.ChangeNpc(w, Ref(f.GetProperty("a")), Ref(f.GetProperty("b")), f.GetProperty("delta").GetInt32()),
                "marked_officer.json.gz" => officerLifecycle.ApplyMarked(Ref(f.GetProperty("person"))!),
                "free_officer.json.gz" or "free_destination.json.gz" => officerLifecycle.ApplyFree(Ref(f.GetProperty("person"))!,
                    Ref(f.GetProperty("release_officer")), Ref(f.GetProperty("relocation"))),
                "marked_capture.json.gz" => OfficerCapture.ApplyMarkedCapture(w, officerLifecycle, Ref(f.GetProperty("person"))),
                "marked_free_capture.json.gz" => OfficerCapture.ApplyFreeMarkedCapture(w, lifecycle,
                    Ref(f.GetProperty("person")), Ref(f.GetProperty("unit")), Ref(f.GetProperty("force")), Ref(f.GetProperty("officer"))),
                "officer_corps.json.gz" => OfficerCapture.ApplySurvivingForce(w, lifecycle,
                    Ref(f.GetProperty("person")), Ref(f.GetProperty("unit")), Ref(f.GetProperty("force")), Ref(f.GetProperty("officer"))),
                "corps_fall.json.gz" => lifecycle.CheckSurvivingForce(source, f.GetProperty("mode").GetInt32()),
                _ => lifecycle.Merge(source!, w.Table(Tables.Corps)[f.GetProperty("destination").GetInt32()], f.GetProperty("mode").GetInt32()),
            };
            int Destroy()
            {
                Assert.Equal(0, f.GetProperty("mode").GetInt32());
                Assert.Equal(4, f.GetProperty("reason").GetInt32());
                Assert.Equal(JsonValueKind.Null, f.GetProperty("attacker").ValueKind);
                Assert.Equal(JsonValueKind.Null, f.GetProperty("attacker_base").ValueKind);
                destruction.ApplyUnopposedRemoval(Ref(f.GetProperty("unit")));
                return 0;
            }
            int Inherit()
            {
                Rec person = Ref(f.GetProperty("person"))!;
                Rec? receiver = Ref(f.GetProperty("receiver"));
                if (!Objects.Valid(w, receiver))
                    Assert.Equal(c.GetProperty("inheritance_candidates").EnumerateArray().Select(Ref), inheritance.OrderedCandidates(person));
                CommandCancellation.Command? command = null;
                var input = f.GetProperty("command");
                if (input.ValueKind != JsonValueKind.Null)
                {
                    if (input.TryGetProperty("index", out var existing))
                    {
                        command = commands[existing.GetInt32()];
                        foreach (var poke in input.GetProperty("pokes").EnumerateArray())
                            Convert.FromHexString(poke[1].GetString()!).CopyTo(command.Data.Bytes, poke[0].GetInt32());
                    }
                    else
                    {
                        byte[] data = new byte[0x88];
                        BitConverter.GetBytes(input.GetProperty("id").GetInt32()).CopyTo(data, 0x58);
                        BitConverter.GetBytes((ushort)input.GetProperty("item").GetInt32()).CopyTo(data, 0x72);
                        command = new(input.GetProperty("kind").GetInt32(), new(-1, data),
                            input.GetProperty("officers").EnumerateArray().Select(e => e.GetInt32()).ToArray(), 0, 0, 0, 0);
                    }
                }
                inheritance.Apply(person, receiver, command);
                return 0;
            }
            if (filename == "unit_destruction.json.gz" || c.TryGetProperty("unit_refreshes", out _))
            {
                Assert.Equal(c.GetProperty("unit_refreshes").EnumerateArray().Select(Ref), unitRefreshes);
                Assert.Equal(c.GetProperty("morale_visuals").EnumerateArray().Select(r => (r[0].GetInt32(), r[1].GetInt32())), moraleVisuals);
                Assert.Equal(c.GetProperty("difficulty_logs").EnumerateArray().Select(r =>
                    new UnitDestruction.DifficultyLog(r[0].GetInt32(), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt32())), difficultyLogs);
            }
            if (filename == "officer_corps.json.gz")
            {
                Assert.Equal(name == "merge_before_prisoner_status" ? new[] { 0 } : new[] { 0, 1 },
                    c.GetProperty("reevaluations").EnumerateArray().Select(e => e[1].GetInt32()));
                Assert.Equal(name == "retained_leader_capture" ? 0 : 1, c.GetProperty("mergers").GetArrayLength());
            }
            if (filename is "officer_death_corps.json.gz" or "officer_death_corps_unit.json.gz")
            {
                if (filename == "officer_death_corps.json.gz") Assert.Equal(0, f.GetProperty("destroy_own_unit").GetInt32());
                Assert.Equal(f.GetProperty("expected_merges").GetInt32(), c.GetProperty("mergers").GetArrayLength());
                Assert.Equal(f.GetProperty("expected_checks").GetInt32(), c.GetProperty("reevaluations").GetArrayLength());
                Assert.All(c.GetProperty("reevaluations").EnumerateArray(), check => Assert.Equal(0, check[1].GetInt32()));
            }
            int expectedResult = unchecked((int)c.GetProperty("result").GetInt64());
            Assert.Equal(expectedResult, result);
            if (filename == "free_consumer.json.gz")
            {
                byte[] expectedManager = Convert.FromHexString(c.GetProperty("after").TryGetProperty("free_manager", out var manager)
                    ? manager.GetString()! : snapshot.GetProperty("free_manager").GetString()!);
                byte[] actualManager = Convert.FromHexString(snapshot.GetProperty("free_manager").GetString()!);
                BitConverter.GetBytes(result).CopyTo(actualManager, 8);
                Assert.Equal(expectedManager, actualManager);
            }
            Assert.Equal(c.GetProperty("allocations").GetArrayLength(), allocations);
            totalAllocations += allocations;
            Assert.Equal(c.GetProperty("cancelled").EnumerateArray().Select(e => Address(e.GetString()!)), disposed);
            Assert.Equal(disposed, detached);
            Assert.Equal(0, refreshes);
            foreach (var native in c.GetProperty("created").EnumerateArray())
            {
                var command = Assert.Single(storage.Active, x => x.Data.NativeAddress == Address(native.GetProperty("address").GetString()!));
                byte[] expected = Convert.FromHexString(native.GetProperty("data").GetString()!);
                var physical = root.GetProperty("physical_fields").EnumerateArray().Select(p => (Start: p[0].GetInt32(), Length: p[1].GetInt32())).ToArray();
                for (int at = 0; at < expected.Length; at++)
                    if (!physical.Any(p => at >= p.Start && at < p.Start + p.Length))
                        Assert.True(expected[at] == command.Data.Bytes[at], $"{name}: return +{at:X}, expected {expected[at]:X2}, got {command.Data.Bytes[at]:X2}");
                var descriptors = native.GetProperty("descriptors").EnumerateArray().ToArray();
                Assert.Equal(descriptors.Length, command.Destinations!.Count);
                for (int i = 0; i < descriptors.Length; i++)
                {
                    Rec d = new(0, Convert.FromHexString(descriptors[i].GetString()!));
                    Assert.Equal(BitConverter.ToUInt64(d.Bytes, 0), command.Destinations[i].Primary.NativeAddress);
                    Assert.Equal(d.I32(8), command.Destinations[i].PrimaryKind);
                    Assert.Equal(d.I32(12), command.Destinations[i].SecondaryKind);
                    Assert.Equal(BitConverter.ToUInt64(d.Bytes, 16), command.Destinations[i].Secondary.NativeAddress);
                }
            }
            var store = c.GetProperty("store");
            Assert.Equal(store.GetProperty("free_head").GetUInt16(), storage.FreeHead);
            Assert.Equal(store.GetProperty("free_ids").EnumerateArray().Select(e => e.GetUInt16()), storage.FreeIds);
            Assert.Equal(store.GetProperty("by_id").EnumerateObject().Select(e => (int.Parse(e.Name), Address(e.Value.GetString()!))).Order(),
                storage.ById.Select(e => (e.Key, e.Value.Data.NativeAddress)).Order());
            Assert.Equal(store.GetProperty("active").EnumerateArray().Select(e => Address(e.GetString()!)), storage.Active.Select(e => e.Data.NativeAddress));
            Assert.Equal(store.GetProperty("pending").EnumerateArray().Select(e => Address(e.GetString()!)), storage.Pending.Select(e => e.Data.NativeAddress));
            var after = c.GetProperty("after");
            if (snapshot.TryGetProperty("initial_rng", out var seed))
            {
                string expectedRng = after.TryGetProperty("rng", out var nextRng) ? nextRng.GetString()! : seed.GetString()!;
                Assert.Equal(BitConverter.ToInt32(Convert.FromHexString(expectedRng)), rng.State);
            }
            foreach (var r in after.GetProperty("records").EnumerateArray()) records[r[0].GetString()!][r[1].GetInt32()] = Convert.FromHexString(r[2].GetString()!);
            foreach (var table in records)
                for (int i = 0; i < table.Value.Length; i++)
                {
                    byte[] actual = w.Table(Convert.ToInt32(table.Key[2..], 16))[i].Bytes;
                    Assert.True(table.Value[i].SequenceEqual(actual), $"{name}: {table.Key}[{i}] differs at " +
                        string.Join(",", table.Value[i].Select((b, at) => (b, at)).Where(x => x.b != actual[x.at]).Select(x => $"{x.at:X}")));
                }
            foreach (var l in snapshot.GetProperty("lists").EnumerateObject())
            {
                var expected = after.TryGetProperty("lists", out var changed) && changed.TryGetProperty(l.Name, out var value)
                    ? value.EnumerateArray().Select(n => n.EnumerateArray().Select(Ref).ToArray()).ToArray() : initialLists[l.Name];
                var actual = w.List(Convert.ToInt32(l.Name[2..], 16));
                Assert.Equal(expected.Length, actual.Count);
                for (int i = 0; i < actual.Count; i++) Assert.Equal(expected[i], actual[i]);
            }
            var nested = after.TryGetProperty("nested", out var changes) ? changes : snapshot.GetProperty("nested");
            foreach (var p in nested.EnumerateObject())
                if (p.Name.StartsWith("caution:")) Assert.Equal(p.Value.EnumerateArray().Select(e => (Rec?)pointers[Address(e.GetString()!)]), members[int.Parse(p.Name[8..])]);
                else if (p.Name.StartsWith("force_deferred:")) Assert.Equal(p.Value.EnumerateArray().Select(e => (Rec?)pointers[Address(e.GetString()!)]), deferred[int.Parse(p.Name[15..])]);
                else if (p.Name == "free_officers") Assert.Equal(p.Value.EnumerateArray().Select(e => (Rec?)pointers[Address(e.GetString()!)]), free);
                else if (p.Name.StartsWith("unit:") && filename is "unit_destruction.json.gz" or "officer_death.json.gz" or "officer_death_extended.json.gz" or "officer_death_corps_unit.json.gz")
                    Assert.Equal(p.Value.EnumerateArray().Select(e => Address(e.GetString()!) is var a && a != 0 ? pointers[a] : (Rec?)null), propagation[p.Name]);
                else Assert.Equal(snapshot.GetProperty("nested").GetProperty(p.Name).GetRawText(), p.Value.GetRawText());
            var expectedCache = after.TryGetProperty("corps_cache", out var map) ? map : snapshot.GetProperty("corps_cache");
            Assert.Equal(expectedCache.EnumerateObject().Select(p => (Address(p.Name), Address(p.Value.GetString()!))).Order(), cache.Select(p => (p.Key, p.Value)).Order());
            var actions = after.TryGetProperty("cmdactions", out var changedActions) ? changedActions : snapshot.GetProperty("cmdactions");
            Assert.Equal(actions.EnumerateArray().Select(p => (p.GetProperty("kind").GetInt32(), string.Join(",", p.GetProperty("officers").EnumerateArray().Select(x => x.GetInt32())))),
                w.CmdActions.Select(p => (p.Kind, string.Join(",", p.Officers))));
            Assert.Equal(worldBefore, w.WorldState.Bytes);
            Assert.Equal(settingsBefore, w.Settings.Bytes);
            if (snapshot.TryGetProperty("initial_rng", out _))
            {
                // The 0x1000-byte settings capture overlaps the separately modelled global RNG at RVA0x1A47B90.
                byte[] actualMemory = w.Settings.Bytes.ToArray();
                int rngOffset = Rng.StateRva - snapshot.GetProperty("exe_settings_rva").GetInt32();
                BitConverter.GetBytes(rng.State).CopyTo(actualMemory, rngOffset);
                byte[] expectedMemory = after.TryGetProperty("settings", out var memory)
                    ? Convert.FromHexString(memory.GetString()!) : settingsBefore;
                Assert.Equal(expectedMemory, actualMemory);
            }
            foreach (var table in snapshot.GetProperty("tables").EnumerateObject())
                if (table.Value.TryGetProperty("extra", out var extra))
                    foreach (var p in extra.EnumerateObject())
                        if (p.Value.TryGetProperty("vec1D0", out var input))
                        {
                            string expected = after.TryGetProperty("officer_counties", out var arrays) && arrays.TryGetProperty(p.Name, out var output)
                                ? output.GetString()! : input.GetString()!;
                            Assert.Equal(Convert.FromHexString(expected), w.Table(Convert.ToInt32(table.Name[2..], 16))[int.Parse(p.Name)].Vector1D0);
                        }
            foreach (var section in after.EnumerateObject()) Assert.Contains(section.Name, new[] { "records", "lists", "nested", "corps_cache", "cmdactions", "officer_counties", "free_manager", "rng", "settings" });
        }
        if (filename is "marked_officer.json.gz" or "marked_capture.json.gz" or "marked_free_capture.json.gz" or "free_officer.json.gz" or "free_destination.json.gz" or "free_consumer.json.gz" or "officer_death.json.gz" or "death_inheritance.json.gz" or "force_relationships.json.gz" or "officer_death_attacker.json.gz") Assert.Equal(0, totalAllocations);
        else Assert.True(totalAllocations > 0);
    }
}
