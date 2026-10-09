using System.Text;
using System.Text.Json;
using System.Buffers.Binary;
using San14Tenkazu.Formulas;

namespace San14Tenkazu.Tests.Formulas;

// Demo play against the core: source/Reverse/observe.py records polling windows, the resolution list and dynamic
// inputs. With SAN14_OBSERVATION this test applies the captured records in order and compares window totals.
// New nonempty tactic queues/results are also replayed from their recorded source/target context.
// Polling does not identify which records ran in each window; differences are exploratory results, not proof of
// a formula error or a missing tactic. The test writes report.txt and does not assert that every window matches.
public class ObservationReport
{
    [Fact]
    public void ReplaysTheObservedSteps()
    {
        string? dir = Environment.GetEnvironmentVariable("SAN14_OBSERVATION");
        if (string.IsNullOrEmpty(dir)) return;
        Assert.True(File.Exists(Path.Combine(dir, "events.jsonl")), $"Missing events.jsonl in {dir}");
        Assert.True(File.Exists(Path.Combine(dir, "world.json.gz")), $"Missing world.json.gz in {dir}");
        World w = LoadWorld(Path.Combine(dir, "world.json.gz"));
        Rec[] units = w.Table(Tables.Units), cities = w.Table(Tables.Cities), gates = w.Table(Tables.Gates), buildings = w.Table(Tables.Buildings);
        var report = new StringBuilder();
        int events = 0, predicted = 0, exact = 0, close = 0;
        int legacy = 0, changedLists = 0, unattributed = 0;
        int callerRejected = 0, noReachBasic = 0;
        int cachedCombatWindows = 0, cachedCombatRecords = 0, incompleteCombatWindows = 0;
        int missingSplits = 0, missingLists = 0, tacticWindows = 0, incompleteTactics = 0;
        int tacticTransitions = 0, populatedTacticTransitions = 0, tacticReplays = 0, exactTactics = 0, omittedTactics = 0;
        var baseline = new Dictionary<int, byte[][]>();
        Rec BaseRec(int id) => id < 0x34 ? cities[id] : gates[id - 0x33];
        foreach (string line in File.ReadLines(Path.Combine(dir, "events.jsonl")))
        {
            using var document = JsonDocument.Parse(line);
            JsonElement ev = document.RootElement;
            events++;
            if (ev.TryGetProperty("records", out JsonElement records))
            {
                JsonElement capturedTacticsAfter = default;
                bool replayTactic = false;
                bool combatComplete = true;
                JsonElement combatBefore = default, combatAfter = default;
                if (ev.TryGetProperty("combat_before", out combatBefore) && ev.TryGetProperty("combat_after", out combatAfter))
                {
                    cachedCombatWindows++;
                    cachedCombatRecords += ValidateCombat(combatBefore) + ValidateCombat(combatAfter);
                    combatComplete = combatBefore.GetProperty("complete").GetBoolean() && combatAfter.GetProperty("complete").GetBoolean();
                    if (!combatComplete) incompleteCombatWindows++;
                }
                foreach (JsonProperty table in ev.GetProperty("input_tables").EnumerateObject())
                {
                    int key = Convert.ToInt32(table.Name[2..], 16);
                    Rec[] target = w.Table(key);
                    if (!baseline.TryGetValue(key, out byte[][]? bytes))
                        baseline[key] = bytes = target.Select(r => (byte[])r.Bytes.Clone()).ToArray();
                    for (int i = 0; i < target.Length; i++) Array.Copy(bytes[i], target[i].Bytes, bytes[i].Length);
                    foreach (JsonProperty record in table.Value.EnumerateObject()) Overlay(target[int.Parse(record.Name)], record.Value.GetString()!);
                }
                Overlay(w.WorldState, ev.GetProperty("worldstate").GetString()!);
                Overlay(w.Settings, ev.GetProperty("settings").GetString()!);
                if (ev.TryGetProperty("flags", out JsonElement flags))
                    foreach (JsonProperty flag in flags.EnumerateObject()) w.SetValue(flag.Name, flag.Value.GetInt32());
                if (ev.TryGetProperty("tactics_before", out JsonElement tacticsBefore) && ev.TryGetProperty("tactics_after", out JsonElement tacticsAfter))
                {
                    tacticWindows++;
                    bool complete = tacticsBefore.GetProperty("complete").GetBoolean() && tacticsAfter.GetProperty("complete").GetBoolean();
                    if (!complete) incompleteTactics++;
                    bool changed = Compact(tacticsBefore) != Compact(tacticsAfter);
                    bool resultsChanged = Compact(tacticsBefore.GetProperty("queue")) != Compact(tacticsAfter.GetProperty("queue"))
                        || Compact(tacticsBefore.GetProperty("rows")) != Compact(tacticsAfter.GetProperty("rows"));
                    if (changed) tacticTransitions++;
                    if (resultsChanged && tacticsAfter.GetProperty("queue").GetArrayLength() > 0)
                    {
                        populatedTacticTransitions++;
                        replayTactic = complete && combatComplete;
                        capturedTacticsAfter = tacticsAfter;
                        if (!complete || !combatComplete) omittedTactics++;
                    }
                    report.AppendLine($"t={ev.GetProperty("t")}: tactic evidence complete={complete}, changed={changed}, queue/results changed={resultsChanged}; before {tacticsBefore}; after {tacticsAfter}");
                }
                if (ev.GetProperty("records_changed").GetBoolean())
                {
                    changedLists++;
                    report.AppendLine($"t={ev.GetProperty("t")}: resolution list changed; before {ev.GetProperty("records_before")}; after {ev.GetProperty("records_after")}");
                }
                if (ev.TryGetProperty("input_lists", out JsonElement inputLists))
                {
                    foreach (JsonProperty capturedList in inputLists.EnumerateObject())
                    {
                        List<Rec?[]> target = w.List(Convert.ToInt32(capturedList.Name[2..], 16));
                        target.Clear();
                        foreach (JsonElement item in capturedList.Value.EnumerateArray())
                            target.Add(item.EnumerateArray().Select(ref_ => ref_.ValueKind == JsonValueKind.Null ? null :
                                w.Table(Convert.ToInt32(ref_[0].GetString()![2..], 16))[ref_[1].GetInt32()]).ToArray());
                    }
                }
                else
                {
                    missingLists++;
                    // Older captures contain only the association list; the other two retain the baseline.
                    List<Rec?[]> target = w.List(Lists.Engagements);
                    target.Clear();
                    foreach (JsonElement item in ev.GetProperty("list").EnumerateArray())
                    {
                        int a = item[0].GetInt32(), b = item[1].GetInt32(), u = item[2].GetInt32();
                        target.Add([a > 0 && a < units.Length ? units[a] : null, b > 0 && b < 0x3E ? BaseRec(b) : null,
                            u > 0 && u < units.Length ? units[u] : null]);
                    }
                }
                if (replayTactic)
                {
                    bool? match = ReplayTactic(w, ev, capturedTacticsAfter, report);
                    if (match is null) omittedTactics++;
                    else { tacticReplays++; if (match.Value) exactTactics++; }
                }
                if (!combatComplete)
                {
                    report.AppendLine($"t={ev.GetProperty("t")}: numerical comparison omitted; incomplete resolution-list reads; before {combatBefore}; after {combatAfter}");
                    continue;
                }
                if (!ev.TryGetProperty("split_counts", out _) && records.EnumerateArray().Any(e =>
                    (Side(e[0]).Split == 0 || Side(e[1]).Split == 0) && Side(e[1]).Kind is 0x1B or 5 or 6 &&
                    Engagement.CanResolve(w, Side(e[0]), Side(e[1]))))
                {
                    missingSplits++;
                    report.AppendLine($"t={ev.GetProperty("t")}: numerical comparison omitted; zero split requires the uncaptured manager +0x10 list");
                    continue;
                }
                var result = ReplayRecords(w, ev, records, report);
                predicted += result.Predicted;
                exact += result.Exact;
                close += result.Close;
                unattributed += result.Unattributed;
                callerRejected += result.CallerRejected;
                noReachBasic += result.NoReachBasic;
                continue;
            }
            legacy++;
            // the objects as they stood before the step
            foreach (JsonProperty p in ev.GetProperty("unit").EnumerateObject()) Overlay(units[int.Parse(p.Name)], p.Value.GetString()!);
            foreach (JsonProperty p in ev.GetProperty("base").EnumerateObject()) Overlay(BaseRec(int.Parse(p.Name)), p.Value.GetString()!);
            var list = ev.GetProperty("list").EnumerateArray().Select(e => (A: e[0].GetInt32(), B: e[1].GetInt32(), U: e[2].GetInt32())).ToList();
            var legacyUnits = new HashSet<int>();
            var legacyBases = new HashSet<int>();
            var legacyBuildings = new HashSet<int>();
            foreach (JsonProperty p in ev.GetProperty("after_unit").EnumerateObject())
            {
                int target = int.Parse(p.Name);
                byte[] after = Convert.FromHexString(p.Value.GetString()!);
                int troopsAfter = BitConverter.ToUInt16(after, 0x16), woundedAfter = BitConverter.ToUInt16(after, 0x18), moraleAfter = after[0x1A];
                Rec unit = units[target];
                foreach (var e in list.Where(e => e.U == target && e.A >= 1))
                {
                    int splitA = list.Count(x => x.A == e.A || x.U == e.A), splitB = list.Count(x => x.U == target || x.A == target);
                    Rec attacker = units[e.A];
                    var a = new Engagement.Side(e.A, 0x1B, attacker.U16(0x2A), Objects.UnitForce(w, attacker), splitA);
                    var b = new Engagement.Side(target, 0x1B, unit.U16(0x2A), Objects.UnitForce(w, unit), splitB);
                    bool fight = Engagement.CanResolve(w, a, b);
                    if (!fight) continue;
                    legacyUnits.Add(target);
                    Engagement.Powers pw = Engagement.Compute(w, a, b);
                    Engagement.UnitAfter ua = Engagement.ApplyUnit(w, unit, pw.Damage, a);
                    predicted++;
                    int lost = unit.U16(0x16) - troopsAfter;
                    bool same = ua.Troops == troopsAfter && ua.Wounded == woundedAfter && ua.Morale == moraleAfter;
                    if (same) exact++; else if (ua.Troops == troopsAfter) close++;
                    report.AppendLine($"t={ev.GetProperty("t")} unit {e.A} -> unit {target}: fight={fight} power {pw.PowerA:F1}/{pw.PowerB:F1} atk {pw.AttackA:F1} def {pw.DefenseB:F1} " +
                        $"damage {pw.Damage} (observed loss {lost}) | troops {ua.Troops} vs {troopsAfter}, wounded {ua.Wounded} vs {woundedAfter}, morale {ua.Morale} vs {moraleAfter}{(same ? "" : "   <-- differs")}");
                }
            }
            foreach (JsonProperty p in ev.GetProperty("after_base").EnumerateObject())
            {
                int id = int.Parse(p.Name);
                byte[] after = Convert.FromHexString(p.Value.GetString()!);
                Rec baseRec = BaseRec(id);
                int troopsAfter = BitConverter.ToInt32(after, 0x3C), woundedAfter = BitConverter.ToInt32(after, 0x40), moraleAfter = after[0x44];
                foreach (var e in list.Where(e => e.B == id && e.A >= 1))
                {
                    Rec attacker = units[e.A];
                    var a = new Engagement.Side(e.A, 0x1B, attacker.U16(0x2A), Objects.UnitForce(w, attacker), list.Count(x => x.A == e.A));
                    var b = new Engagement.Side(id < 0x34 ? id : id - 0x33, id < 0x34 ? 5 : 6, Map.BaseHex(w, baseRec), Map.BaseForce(w, baseRec), 1);
                    if (!Engagement.CanResolve(w, a, b)) continue;
                    legacyBases.Add(id);
                    Engagement.Powers pw = Engagement.Compute(w, a, b);
                    Engagement.BaseAfter ba = Engagement.ApplyBase(w, baseRec, pw.Damage, a);
                    predicted++;
                    bool same = ba.Troops == troopsAfter && ba.Wounded == woundedAfter && ba.Morale == moraleAfter;
                    if (same) exact++; else if (ba.Troops == troopsAfter) close++;
                    report.AppendLine($"t={ev.GetProperty("t")} unit {e.A} -> base {id}: damage {pw.Damage} | troops {ba.Troops} vs {troopsAfter}, wounded {ba.Wounded} vs {woundedAfter}, morale {ba.Morale} vs {moraleAfter}{(same ? "" : "   <-- differs")}");
                }
            }
            foreach (JsonProperty p in ev.GetProperty("after_building").EnumerateObject())
            {
                int bid = int.Parse(p.Name);
                Overlay(buildings[bid], ev.GetProperty("building").GetProperty(p.Name).GetString()!);
                int durabilityAfter = BitConverter.ToUInt16(Convert.FromHexString(p.Value.GetString()!), 0x14);
                foreach (var e in list.Where(e => e.B >= 1 && BaseRec(e.B).U16(0x4E) == bid && e.A >= 1))
                {
                    Rec attacker = units[e.A];
                    var a = new Engagement.Side(e.A, 0x1B, attacker.U16(0x2A), Objects.UnitForce(w, attacker), 1);
                    var b = new Engagement.Side(e.B < 0x34 ? e.B : e.B - 0x33, e.B < 0x34 ? 5 : 6, buildings[bid].U16(0x10), buildings[bid].U8(0x13), 1);
                    if (!Reach.CanAttack(w, a.Hex, b.Hex)) continue;
                    legacyBuildings.Add(bid);
                    Bombard.Result r = Engagement.Siege(w, a, b);
                    predicted++;
                    bool same = r.Durability == durabilityAfter;
                    if (same) exact++;
                    report.AppendLine($"t={ev.GetProperty("t")} unit {e.A} -> building {bid}: durability {r.Durability} vs {durabilityAfter}{(same ? "" : "   <-- differs")}");
                }
            }
            unattributed += ev.GetProperty("after_unit").EnumerateObject().Count(p => !legacyUnits.Contains(int.Parse(p.Name)));
            unattributed += ev.GetProperty("after_base").EnumerateObject().Count(p => !legacyBases.Contains(int.Parse(p.Name)));
            unattributed += ev.GetProperty("after_building").EnumerateObject().Count(p => !legacyBuildings.Contains(int.Parse(p.Name)));
        }
        report.Insert(0, $"{events} events, {predicted} predictions, {exact} exact, {close} with the troops right\n" +
            $"{legacy} legacy windows, {changedLists} windows with a changed resolution list, {unattributed} changes without a modelled defender\n" +
            $"{missingSplits} windows omitted because the manager split-count input was not captured; {missingLists} windows lack the full combat lists\n" +
            $"{tacticWindows} windows include tactic queue/result evidence; {incompleteTactics} of these have incomplete reads\n" +
            $"{tacticTransitions} tactic-state transitions, {populatedTacticTransitions} with changed nonempty queue/results; retained evidence is not a new invocation\n" +
            $"{tacticReplays} tactic queue/result replays, {exactTactics} exact, {omittedTactics} omitted because the reads or record context were incomplete/ambiguous\n" +
            $"{callerRejected} records rejected by captured caller admission inputs; {noReachBasic} records route to basic damage after failed siege reach\n" +
            $"{cachedCombatWindows} windows retain raw combat records and cached troop words ({cachedCombatRecords} before/after records); {incompleteCombatWindows} windows omitted because combat reads were incomplete\n" +
            "Polling does not identify which captured records ran in each window; differences alone do not identify a formula error, tactic or link effect.\n\n");
        File.WriteAllText(Path.Combine(dir, "report.txt"), report.ToString());
    }

    // A new populated result can be checked against the previous poll's inputs when its source/target context
    // identifies exactly one recorded pair. This comparison does not establish an atomic input snapshot or the
    // timing of deferred damage. Restore every dynamic table before replaying ordinary engagement damage.
    private static bool? ReplayTactic(World w, JsonElement ev, JsonElement captured, StringBuilder report)
    {
        if (!captured.TryGetProperty("context", out JsonElement context)) return null;
        bool Matches(Engagement.Side side, JsonElement unit, JsonElement baseRef)
        {
            JsonElement reference = side.Kind == 27 ? unit : baseRef;
            int table = side.Kind switch { 27 => Tables.Units, 5 => Tables.Cities, 6 => Tables.Gates, _ => 0 };
            return reference.ValueKind == JsonValueKind.Array && table != 0
                && Convert.ToInt32(reference[0].GetString()![2..], 16) == table && reference[1].GetInt32() == side.Index;
        }
        // 0x172E20 reads kind/index/hex/force, but not the ordinary-damage split byte at side +0x14.
        var pairs = new[] { ev.GetProperty("records_before"), ev.GetProperty("records_after") }
            .SelectMany(list => list.EnumerateArray())
            .Select(pair => (A: Side(pair[0]) with { Split = 0 }, B: Side(pair[1]) with { Split = 0 }))
            .Where(pair => Matches(pair.A, context[0], context[1]) && Matches(pair.B, context[2], context[3])).Distinct().ToArray();
        if (pairs.Length != 1)
        {
            report.AppendLine($"t={ev.GetProperty("t")}: tactic replay omitted; context identifies {pairs.Length} distinct recorded pairs");
            return null;
        }
        var before = ev.GetProperty("input_tables").EnumerateObject().Select(table => Convert.ToInt32(table.Name[2..], 16))
            .ToDictionary(table => table, table => w.Table(table).Select(record => (byte[])record.Bytes.Clone()).ToArray());
        Rec?[][] pending = w.List(Lists.Engagements).ToArray();
        try
        {
            TacticFlow.Resolution flow = TacticFlow.Apply(w, pairs[0].A, pairs[0].B);
            object queue = flow.Commands.Select(command => new object[] { command.Kind, command.Number, command.Tactic.Index,
                command.Targets.Select(targets => targets.Select(tile => tile.Index).ToArray()).ToArray() }).ToArray();
            var rows = new SortedDictionary<int, object>();
            for (int i = 0; i < flow.Applications.Count; i++)
            {
                var application = flow.Applications[i]; var command = flow.Commands[i];
                rows[application.ResultIndex] = new object[] { application.ResultIndex, command.Tactic.Index, command.Kind, command.Number,
                    application.Slots.Select(slot => slot.Select(result => new[] { result.Kind, result.Number, result.Hex, result.Amount }).ToArray()).ToArray() };
            }
            string computedQueue = JsonSerializer.Serialize(queue), computedRows = JsonSerializer.Serialize(rows.Values.ToArray());
            bool match = computedQueue == Compact(captured.GetProperty("queue")) && computedRows == Compact(captured.GetProperty("rows"));
            report.AppendLine($"t={ev.GetProperty("t")}: tactic flow queue/results match={match}, commands={flow.Commands.Count}");
            if (!match) report.AppendLine($"  computed queue {computedQueue}; rows {computedRows}");
            return match;
        }
        finally
        {
            foreach (var table in before)
                for (int i = 0; i < table.Value.Length; i++) Array.Copy(table.Value[i], w.Table(table.Key)[i].Bytes, table.Value[i].Length);
            List<Rec?[]> target = w.List(Lists.Engagements); target.Clear(); target.AddRange(pending);
        }
    }

    private static string Compact(JsonElement element) => JsonSerializer.Serialize(element);

    private static (int Predicted, int Exact, int Close, int Unattributed, int CallerRejected, int NoReachBasic) ReplayRecords(World w, JsonElement ev, JsonElement records, StringBuilder report)
    {
        Rec[] units = w.Table(Tables.Units), cities = w.Table(Tables.Cities), gates = w.Table(Tables.Gates), buildings = w.Table(Tables.Buildings);
        var seenUnits = new HashSet<int>();
        var seenBases = new HashSet<int>();
        var seenBuildings = new HashSet<int>();
        int callerRejected = 0, noReachBasic = 0;
        var pairs = records.EnumerateArray().Select(e => (A: Side(e[0]), B: Side(e[1]))).ToArray();
        Rec BaseRec(Engagement.Side s) => s.Kind == 5 ? cities[s.Index] : gates[s.Index];
        int BaseId(Engagement.Side s) => s.Kind == 5 ? s.Index : s.Index + 0x33;
        Engagement.Side Split(Engagement.Side s)
        {
            if (s.Split != 0) return s;
            // 0x16A081..0x16A0B7 searches manager +0x10 by kind and index, falling back to 1.
            int count = 1;
            if (ev.TryGetProperty("split_counts", out JsonElement counts))
                foreach (JsonElement entry in counts.EnumerateArray())
                    if (entry[0].GetInt32() == s.Index && entry[1].GetInt32() == s.Kind) { count = entry[2].GetInt32(); break; }
            return s with { Split = count };
        }
        foreach (var pair in pairs)
        {
            Engagement.Side a = Split(pair.A), b = Split(pair.B);
            if (!Engagement.CanResolve(w, a, b)) { callerRejected++; continue; }
            if (a.Kind is not (0x1B or 5 or 6)) continue;
            Engagement.StartResult start = Engagement.Start(w, a, b);
            if (start.Building is { } building && start.Siege is { } siege)
            {
                SetU16(building, 0x14, siege.Durability);
                seenBuildings.Add(building.Index);
            }
            if (start.NextStep != 2) continue;
            if (!Reach.CanAttack(w, a.Hex, b.Hex)) noReachBasic++;
            Engagement.Powers powers = Engagement.Compute(w, a, b);
            if (b.Kind == 0x1B)
            {
                Rec unit = units[b.Index];
                Engagement.UnitAfter after = Engagement.ApplyUnit(w, unit, powers.Damage, a);
                SetU16(unit, 0x16, after.Troops);
                SetU16(unit, 0x18, after.Wounded);
                unit.Bytes[0x1A] = (byte)after.Morale;
                SetI32(unit, 0x5C, after.Gold);
                SetI32(unit, 0x60, after.Food);
                Rec? cmd = w.OfficerById(unit.U16(0x12));
                if (cmd is not null) SetU16(cmd, 0x184, after.Lit);
                seenUnits.Add(b.Index);
            }
            else if (b.Kind is 5 or 6)
            {
                Rec baseRec = BaseRec(b);
                Engagement.BaseAfter after = Engagement.ApplyBase(w, baseRec, powers.Damage, a);
                SetI32(baseRec, 0x3C, after.Troops);
                SetI32(baseRec, 0x40, after.Wounded);
                baseRec.Bytes[0x44] = (byte)after.Morale;
                seenBases.Add(BaseId(b));
            }
        }
        int predicted = 0, exact = 0, close = 0, unattributed = 0;
        foreach (JsonProperty p in ev.GetProperty("after_unit").EnumerateObject())
        {
            int id = int.Parse(p.Name);
            if (!seenUnits.Contains(id)) { unattributed++; report.AppendLine($"t={ev.GetProperty("t")} unit {id}: no modelled defender in the captured records; {ObservedChange(ev, "unit", p)}"); continue; }
            Rec got = units[id];
            byte[] after = Convert.FromHexString(p.Value.GetString()!);
            int troops = BitConverter.ToUInt16(after, 0x16), wounded = BitConverter.ToUInt16(after, 0x18), morale = after[0x1A];
            bool same = got.U16(0x16) == troops && got.U16(0x18) == wounded && got.U8(0x1A) == morale && got.I32(0x5C) == BitConverter.ToInt32(after, 0x5C) && got.I32(0x60) == BitConverter.ToInt32(after, 0x60);
            predicted++;
            if (same) exact++; else if (got.U16(0x16) == troops) close++;
            report.AppendLine($"t={ev.GetProperty("t")} unit {id}: troops {got.U16(0x16)} vs {troops}, wounded {got.U16(0x18)} vs {wounded}, morale {got.U8(0x1A)} vs {morale}{(same ? "" : "   <-- differs")}");
        }
        foreach (JsonProperty p in ev.GetProperty("after_base").EnumerateObject())
        {
            int id = int.Parse(p.Name);
            if (!seenBases.Contains(id)) { unattributed++; report.AppendLine($"t={ev.GetProperty("t")} base {id}: no modelled defender in the captured records; {ObservedChange(ev, "base", p)}"); continue; }
            Rec got = id < 0x34 ? cities[id] : gates[id - 0x33];
            byte[] after = Convert.FromHexString(p.Value.GetString()!);
            int troops = BitConverter.ToInt32(after, 0x3C), wounded = BitConverter.ToInt32(after, 0x40), morale = after[0x44];
            bool same = got.I32(0x3C) == troops && got.I32(0x40) == wounded && got.U8(0x44) == morale;
            predicted++;
            if (same) exact++; else if (got.I32(0x3C) == troops) close++;
            report.AppendLine($"t={ev.GetProperty("t")} base {id}: troops {got.I32(0x3C)} vs {troops}, wounded {got.I32(0x40)} vs {wounded}, morale {got.U8(0x44)} vs {morale}{(same ? "" : "   <-- differs")}");
        }
        foreach (JsonProperty p in ev.GetProperty("after_building").EnumerateObject())
        {
            int id = int.Parse(p.Name);
            if (!seenBuildings.Contains(id)) { unattributed++; report.AppendLine($"t={ev.GetProperty("t")} building {id}: no modelled defender in the captured records; {ObservedChange(ev, "building", p)}"); continue; }
            int expected = BitConverter.ToUInt16(Convert.FromHexString(p.Value.GetString()!), 0x14);
            int got = buildings[id].U16(0x14);
            predicted++;
            if (got == expected) exact++;
            report.AppendLine($"t={ev.GetProperty("t")} building {id}: durability {got} vs {expected}{(got == expected ? "" : "   <-- differs")}");
        }
        return (predicted, exact, close, unattributed, callerRejected, noReachBasic);
    }

    private static string ObservedChange(JsonElement ev, string kind, JsonProperty record)
    {
        byte[] before = Convert.FromHexString(ev.GetProperty(kind).GetProperty(record.Name).GetString()!);
        byte[] after = Convert.FromHexString(record.Value.GetString()!);
        string Health(byte[] bytes) => kind switch
        {
            "unit" => $"troops {BitConverter.ToUInt16(bytes, 0x16)}, wounded {BitConverter.ToUInt16(bytes, 0x18)}, morale {bytes[0x1A]}",
            "base" => $"troops {BitConverter.ToInt32(bytes, 0x3C)}, wounded {BitConverter.ToInt32(bytes, 0x40)}, morale {bytes[0x44]}",
            _ => $"durability {BitConverter.ToUInt16(bytes, 0x14)}",
        };
        return $"observed {Health(before)} -> {Health(after)}";
    }

    private static Engagement.Side Side(JsonElement e) => new(e[0].GetInt32(), e[1].GetInt32(), e[2].GetInt32(), e[3].GetInt32(), e[4].GetInt32());
    private static int ValidateCombat(JsonElement combat)
    {
        int n = 0;
        void CheckSide(JsonElement side, ReadOnlySpan<byte> raw)
        {
            Assert.Equal((int)BinaryPrimitives.ReadUInt16LittleEndian(raw), side[0].GetInt32());
            Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(raw[4..]), side[1].GetInt32());
            Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(raw[0xC..]), side[2].GetInt32());
            Assert.Equal(BinaryPrimitives.ReadInt32LittleEndian(raw[0x10..]), side[3].GetInt32());
            Assert.Equal((int)raw[0x14], side[4].GetInt32());
        }
        foreach (JsonElement record in combat.GetProperty("records").EnumerateArray())
        {
            byte[] raw = Convert.FromHexString(record.GetProperty("record_raw").GetString()!);
            Assert.Equal(0x38, raw.Length);
            CheckSide(record.GetProperty("target"), raw.AsSpan(8, 24));
            Assert.Equal((int)BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(0x12)), record.GetProperty("target_troops").GetInt32());
            JsonElement source = record.GetProperty("source_raw");
            if (source.ValueKind == JsonValueKind.Null)
            {
                Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(raw));
                Assert.Equal(JsonValueKind.Null, record.GetProperty("source").ValueKind);
                Assert.Equal(JsonValueKind.Null, record.GetProperty("source_troops").ValueKind);
            }
            else
            {
                Assert.NotEqual(0UL, BinaryPrimitives.ReadUInt64LittleEndian(raw));
                byte[] bytes = Convert.FromHexString(source.GetString()!);
                Assert.Equal(24, bytes.Length);
                CheckSide(record.GetProperty("source"), bytes);
                Assert.Equal((int)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0xA)), record.GetProperty("source_troops").GetInt32());
            }
            n++;
        }
        return n;
    }
    private static void SetU16(Rec r, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(r.Bytes.AsSpan(offset, 2), (ushort)value);
    private static void SetI32(Rec r, int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(r.Bytes.AsSpan(offset, 4), value);

    private static World LoadWorld(string path)
    {
        // Vectors.LoadWorld reads from the test's Vectors folder; this one takes a path
        string tmp = Path.Combine(Vectors.Dir, "observed-world.json.gz");
        File.Copy(path, tmp, true);
        try { return Vectors.LoadWorld("observed-world.json.gz"); }
        finally { File.Delete(tmp); }
    }

    private static void Overlay(Rec r, string hex)
    {
        byte[] b = Convert.FromHexString(hex);
        Array.Copy(b, r.Bytes, Math.Min(b.Length, r.Bytes.Length));
    }
}
