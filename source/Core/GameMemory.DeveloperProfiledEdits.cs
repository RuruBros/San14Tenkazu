#if LOCAL_DEVELOPER
using System.Security.Cryptography;

namespace San14Tenkazu;

internal static partial class GameMemory
{
    internal static (int Passed, int Missing) RoundTripProfiledFields(GameIdentity identity, GameLayout layout, Action<object> record)
    {
        var profile = identity.DataProfile ?? throw new InvalidOperationException(L.Compatibility.Unavailable);
        var baseline = ReadSnapshot(identity, layout);
        if (baseline is not { Date: not null, PlayerForce: > 0 } || ReadPace(identity, layout) != GamePace.Paused)
            throw new InvalidOperationException(L.DeveloperUi.PauseRequired);
        var countries = ReadCountryNames(identity, layout).Select(c => c.Index).ToArray();
        var fields = profile.Fields.Keys.Where(f => !OfficerFields.Personalities.Contains(f) &&
                f.Record != RecordKind.Item && !ForceFields.IsAgreement(f))
            .OrderBy(f => f.Record).ThenBy(f => f.Offset).ThenBy(f => f.Min).ToArray();
        var covered = new HashSet<FieldDef>();
        var missing = new List<FieldDef>();
        record(new { stage = "started", identity.ProcessId, identity.StartTimeUtc, identity.ExeHash, baseline.PlayerForce,
            baseline.Date, baseline.Hour, registeredFields = fields.Length });
        foreach (var field in fields.Where(f => !OfficerFields.IsExperience(f)))
        {
            if (BuildProfiledRoundTripPlan(baseline, field, countries) is not { } edit)
            {
                missing.Add(field);
                record(new { stage = "no-eligible-target", field = field.Label, kind = field.Record.ToString(), field.Offset });
                continue;
            }
            string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
            object Receipt(string stage, byte[] bytes) => new { stage, field = field.Label, kind = edit.Kind.ToString(), field.Offset,
                edit.Label, edit.Address, sha256 = Hash(bytes), values = edit.Changes.Select(c => new
                { field = c.Field.Label, c.Field.Offset, value = c.Field.Read(bytes) }).ToArray() };
            record(Receipt("planned", edit.Expected));
            var receipts = WithProfiledEditSession(identity, layout, (handle, data) =>
            {
                if (data.Session?.Entries[2].Address != baseline.Session?.Entries[2].Address ||
                    data.Date != baseline.Date || data.Hour != baseline.Hour || data.PlayerForce != baseline.PlayerForce ||
                    BuildProfiledRoundTripPlan(data, field, countries) is not { } fresh || fresh.Address != edit.Address ||
                    !fresh.Expected.AsSpan().SequenceEqual(edit.Expected) || !fresh.Changes.SequenceEqual(edit.Changes))
                    throw new InvalidOperationException(L.Errors.StateMoved);
                var undo = Undo.Of([edit]);
                byte[] ReadRecord() => ReadBytes(handle, edit.Address, edit.Expected.Length);
                byte[] before = ReadRecord();
                if (!before.AsSpan().SequenceEqual(edit.Expected)) throw new InvalidOperationException(L.Errors.StateMoved);
                ApplyProfiledEdits(handle, identity, profile, layout, data, [edit]);
                byte[] changed;
                byte[] restored;
                try { changed = ReadRecord(); }
                finally
                {
                    // Do not overwrite a changed target with baseline bytes. Restore only through the guarded edit path.
                    if (!ReadRecord().AsSpan().SequenceEqual(undo.Single().Expected))
                        throw new InvalidOperationException(L.DeveloperUi.ProfiledFieldsRecoveryFailed);
                    ApplyProfiledEdits(handle, identity, profile, layout, data.With([edit]), undo);
                    restored = ReadRecord();
                    if (!restored.AsSpan().SequenceEqual(before))
                        throw new InvalidOperationException(L.DeveloperUi.ProfiledFieldsRecoveryFailed);
                }
                if (!changed.AsSpan().SequenceEqual(undo.Single().Expected))
                    throw new InvalidDataException(L.Connection.EditCodeChanged);
                return new[] { Receipt("changed-readback", changed), Receipt("restored-byte-exact", restored) };
            });
            foreach (var receipt in receipts) record(receipt);
            foreach (var change in edit.Changes) covered.Add(change.Field);
        }
        foreach (var field in fields.Where(f => OfficerFields.IsExperience(f) && !covered.Contains(f)))
        {
            missing.Add(field);
            record(new { stage = "no-eligible-target", field = field.Label, kind = field.Record.ToString(), field.Offset });
        }
        record(new { stage = missing.Count == 0 ? "all-restored" : "incomplete", registeredFields = fields.Length,
            passed = covered.Count, missing = missing.Count });
        return (covered.Count, missing.Count);
    }

    private static RecordEdit? BuildProfiledRoundTripPlan(GameSnapshot data, FieldDef field, int[] countries)
    {
        int player = data.PlayerForce;
        RecordEdit? Try(byte[] bytes, Func<FieldChange, EditPlan> plan, Func<long, bool>? valid = null)
        {
            long before = field.Read(bytes);
            if (before < field.Min || before > field.Max) return null;
            IEnumerable<long> choices = field == ForceFields.Country ? countries.Select(i => (long)i).Append(0)
                .OrderBy(i => Math.Abs(i - before)) : [before - 1, before + 1];
            foreach (long value in choices)
            {
                if (value == before || value < field.Min || value > field.Max || valid?.Invoke(value) == false) continue;
                var result = plan(new(field, value));
                if (result.Refused || result.Edits is not [var edit] || !edit.Changes.Contains(new(field, value))) continue;
                if (OfficerFields.Abilities.Contains(field) && !edit.Changes.Any(c => c.Field == OfficerFields.ExperienceOf(field))) continue;
                return edit;
            }
            return null;
        }
        IEnumerable<int> cities = data.CitiesOf(player).Order();
        if (field.Record == RecordKind.City)
            return cities.Select(id => Try(data.Cities[id].Data, change => CityEdits.Plan(data, id, new([change], [], null)),
                value => field != CityFields.Troops || value > 0)).OfType<RecordEdit>().FirstOrDefault();
        if (field.Record == RecordKind.County)
            return cities.SelectMany(id => data.Counties.Where(c => c.HomeCity == id && c.CenterTile < data.TileOwners.Length &&
                    data.TileOwners[c.CenterTile] == player).OrderBy(c => c.Index).Select(c => Try(c.Data,
                change => CityEdits.Plan(data, id, new([], [(c.Index, [change])], null))))).OfType<RecordEdit>().FirstOrDefault();
        if (field == CityFields.Durability)
            return cities.Where(data.CastleByCity.ContainsKey).Select(id => Try(data.CastleByCity[id].Data,
                change => CityEdits.Plan(data, id, new([change], [], null)), value => value > 0)).OfType<RecordEdit>().FirstOrDefault();
        if (field == GateFields.Durability || field.Record == RecordKind.Gate)
            return data.GatesOf(player).OrderBy(g => g.Id).Select(g => field.Record == RecordKind.Gate
                ? Try(g.Data, change => GateEdits.Plan(data, g.Id, new([change], null)))
                : g.Building is { } b ? Try(b.Data, change => GateEdits.Plan(data, g.Id, new([change], null)), value => value > 0) : null)
                .OfType<RecordEdit>().FirstOrDefault();
        if (field == FacilityFields.Durability)
            return data.Facilities.Where(f => f.Force == player && f.Built && f.Building is not null).OrderBy(f => f.Index)
                .Select(f => Try(f.Building!.Data, change => FacilityEdits.Plan(data, f.Index, f.Kind, f.Tile, f.Force, change.Value)))
                .OfType<RecordEdit>().FirstOrDefault();
        if (field.Record == RecordKind.Officer)
            return data.OfficersOf(player).Where(o => OfficerFields.IsVassal(o.Status)).OrderBy(o => o.Id)
                .Select(o => Try(o.Data, change => OfficerEdits.Plan(data, o.Id, new([change], null, null, [])), value =>
                {
                    if (OfficerFields.Abilities.Contains(field))
                        return data.OriginalAbilityTableVerified && o[field] <= 100 && value <= 100 && OfficerFields.FitsExperience(o.Data, field);
                    if (!OfficerFields.Years.Contains(field)) return true;
                    long birth = field == OfficerFields.Birth ? value : o[OfficerFields.Birth];
                    long debut = field == OfficerFields.Debut ? value : o[OfficerFields.Debut];
                    long death = field == OfficerFields.Death ? value : o[OfficerFields.Death];
                    return birth is >= 100 and <= 400 && birth <= debut && debut < death;
                })).OfType<RecordEdit>().FirstOrDefault();
        if (field.Record == RecordKind.Unit)
            return data.Units.Where(u => data.ForceOfUnit(u) == player).OrderBy(u => u.Slot).Select(u => Try(u.Data,
                change => UnitEdits.Plan(data, u.Slot, u.Commander, [change]), value => field != UnitFields.Troops || value > 0))
                .OfType<RecordEdit>().FirstOrDefault();
        return field.Record == RecordKind.Force && data.ForceRecords.TryGetValue(player, out var force)
            ? Try(force.Data, change => ForceEdits.Plan(data, player, [change], [])) : null;
    }
}
#endif
