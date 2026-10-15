namespace San14Tenkazu;

// Checks, made when the editor connects, that each group of values it edits is read where it should be: facts any
// game keeps whatever the save (a city's 태수 is an officer of that city, an officer's abilities are the points his
// experience has reached, a force's 친선 with itself is 100). On a build the offsets were not verified on, only the
// groups that pass are written; on the verified build a failure is only reported, as a save may hold what the facts
// did not foresee. The facts that tie records together (Tie) are also checked on what a write would leave, before
// it is written (Broken): a rule that forgot a value it should have written along is caught there.
internal static class GameChecks
{
    internal static readonly string City = L.Checks.GroupCity, County = L.Checks.GroupCounty, Abilities = L.Checks.GroupAbilities, Years = L.Checks.GroupYears, Traits = L.Checks.GroupTraits,
        Ties = L.Checks.GroupTies, Standing = L.Checks.GroupStanding, Force = L.Checks.GroupForce, Item = L.Checks.GroupItem, Unit = L.Checks.GroupUnit, World = L.Checks.GroupWorld, Personality = L.Checks.GroupPersonality,
        PlayRecord = L.Checks.GroupPlayRecord;

    // One fact over the records it applies to: how many fit it, and the share that must. A couple of records may
    // always miss (some of a living game are caught between two states, or were left odd by the game itself, like
    // 32 whose 배우자 800 is married to no one); a value read from the wrong place misses nearly everywhere.
    internal sealed record Check(string Group, string What, int Fit, int Total, double Needed, bool Tie = false)
    {
        private const int Odd = 2;
        internal bool Passed => Total - Fit <= Math.Max(Odd, Total * (1 - Needed));
        internal int Misses => Total - Fit;
        public override string ToString() => $"{What} {Fit:N0}/{Total:N0}";
    }

    // The group a written field belongs to: a write on an unverified build goes through only if its groups passed.
    internal static string GroupOf(FieldDef field) => field.Record switch
    {
        RecordKind.City or RecordKind.Building or RecordKind.Gate => City,
        RecordKind.County => County,
        RecordKind.Force => Force,
        RecordKind.Item => Item,
        RecordKind.Personality => Personality,
        RecordKind.Unit => Unit,
        RecordKind.PlayRecord => PlayRecord,
        _ when field == OfficerFields.Loyalty || OfficerFields.Abilities.Contains(field) || OfficerFields.IsExperience(field) => Abilities,
        _ when OfficerFields.Years.Contains(field) => Years,
        _ when field == OfficerFields.Spouse || field == OfficerFields.Sworn || OfficerFields.Likes.Contains(field) || OfficerFields.Dislikes.Contains(field) => Ties,
        _ when LinkedFields.Contains(field) && field != OfficerFields.Office => Standing,
        _ => Traits
    };

    internal static List<Check> Run(GameSnapshot data, GameCatalog catalog)
    {
        var checks = new List<Check>();
        void Add(string group, string what, IEnumerable<bool> facts, double needed = 1, bool tie = false)
        {
            int fit = 0, total = 0;
            foreach (bool fact in facts)
            {
                total++;
                if (fact) fit++;
            }
            checks.Add(new Check(group, what, fit, total, needed, tie));
        }

        OfficerRecord? Officer(long id) => data.OfficerById.GetValueOrDefault((int)id);
        // The game's own officers (1~999) for the facts about an officer's own values: the generated ones (4000~)
        // and the special ones (2000~ new officers, 3000~ such as 설지군's) keep experience, 주의 and 전법 their own
        // way (read on 2026-10-02: 4201~4248 with 주의 0 and small experience, 3001~3011 with their own tactics).
        OfficerRecord[] named = data.Officers.Where(o => o.Id is >= 1 and < 1000).ToArray();
        OfficerRecord[] active = named.Where(o => OfficerFields.IsActive(o.Status)).ToArray();
        OfficerRecord[] members = data.Officers.Where(o => OfficerFields.IsMember(o.Status)).ToArray();
        CityRecord[] cities = data.Cities.Skip(1).ToArray();

        // 도시: scale and stores in range, the 태수 one of the city's officers, a castle under every city.
        Add(City, L.Checks.CityScale, cities.Select(c => c[CityFields.Scale] is >= 1 and <= 4));
        Add(City, L.Checks.CityStores, cities.Select(c => new[] { CityFields.Gold, CityFields.Food, CityFields.Troops, CityFields.Wounded }
            .All(f => c[f] is >= 0 and <= 10_000_000)));
        Add(City, L.Checks.CityGovernor, cities.Where(c => c.Governor != 0).Select(c => Officer(c.Governor) is { Status: >= 1 and <= 3 } o && o.BaseId == c.CityId), 0.9, tie: true);
        Add(City, L.Checks.GateGovernor, data.Gates.Where(g => g[CityFields.GateGovernor] != 0)
            .Select(g => Officer(g[CityFields.GateGovernor]) is { Status: >= 1 and <= 3 } o && o.BaseId == g.Id), 0.9, tie: true);
        Add(City, L.Checks.CastleBuilding, cities.Select(c => data.CastleByCity.TryGetValue(c.CityId, out BuildingRecord? castle) && castle[CityFields.Durability] <= 60_000), 0.95);

        // 군: development and order in range.
        Add(County, L.Checks.CountyRanges, data.Counties.Select(c => c[CountyFields.PublicOrder] <= 100 &&
            new[] { CountyFields.Commerce, CountyFields.Agriculture, CountyFields.Barracks }.All(f => c[f] <= 1000)));

        // 장수: abilities are what the experience reached; years in order.
        Add(Abilities, L.Checks.AbilityExperience, active.Select(o => data.RuntimeRules is not null && OfficerFields.Abilities.All(a => OfficerFields.FitsExperience(o.Data, a, data.RuntimeRules.Growth))), 0.95);
        Add(Years, L.Checks.YearOrder, named.Select(o => o[OfficerFields.Birth] is >= 100 and <= 400 &&
            o[OfficerFields.Death] > o[OfficerFields.Birth] && o[OfficerFields.Debut] >= o[OfficerFields.Birth]), 0.95);

        // 성향: values within their tables; no two officers of a force hold one office.
        Add(Traits, L.Checks.TraitRanges, active.Select(o => o[OfficerFields.Gender] <= 1 && o[OfficerFields.Ideology] is >= 1 and <= 6 &&
            o[OfficerFields.Policy] <= 120 && o[OfficerFields.Compatibility] <= 149), 0.95);
        Add(Traits, L.Checks.OneOfficeHolder, data.Forces.Keys.Select(force => data.OfficersOf(force)
            .Select(o => o[OfficerFields.Office]).Where(office => office != OfficerFields.NoOffice).GroupBy(office => office).All(g => g.Count() == 1)), tie: true);
        // (A ruler who rose by rebellion holds 0, not 없음: 양수 in 203.)
        Add(Traits, L.Checks.OfficeOnlyVassals, active.Where(o => !OfficerFields.IsVassal(o.Status))
            .Select(o => o[OfficerFields.Office] is OfficerFields.NoOffice or 0), 0.95, tie: true);
        var personalities = catalog.Personalities.Select(p => (long)p.Id).ToHashSet();
        Add(Traits, L.Checks.PersonalityIds, active.SelectMany(o => OfficerFields.Personalities.Select(f => o[f]).Where(id => id != 0))
            .Select(id => personalities.Count == 0 ? id <= 355 : personalities.Contains(id)), 0.98);
        var tactics = catalog.Tactics.Select(t => (long)t.Id).ToHashSet();
        Add(Traits, L.Checks.TacticIds, active.SelectMany(o => OfficerFields.Tactics.Select(f => o[f])).Where(id => id != 0)
            .Select(id => tactics.Count == 0 ? id <= 200 : tactics.Contains(id)), 0.98);

        // 관계: spouses point at each other; a 의형제 group is named after one of its members; ties name officers.
        Add(Ties, L.Checks.SpousesMatch, named.Where(o => o[OfficerFields.Spouse] != 0)
            .Select(o => Officer(o[OfficerFields.Spouse]) is { } spouse && spouse[OfficerFields.Spouse] == o.Id), 0.95, tie: true);
        Add(Ties, L.Checks.SwornGroups, named.Where(o => o[OfficerFields.Sworn] != 0)
            .Select(o => Officer(o[OfficerFields.Sworn]) is { } head && head[OfficerFields.Sworn] == o[OfficerFields.Sworn]), 0.95, tie: true);
        Add(Ties, L.Checks.TiesAreOfficers, named.SelectMany(o => OfficerFields.Likes.Concat(OfficerFields.Dislikes).Select(f => o[f]))
            .Where(id => id != 0).Select(id => Officer(id) is not null), 0.98, tie: true);

        // 소속: a force's officers are in its corps and a base; its ruler leads it; free officers have no corps.
        Add(Standing, L.Checks.MembersCorpsBase, members.Select(o => data.ForceOfCorps(o.Corps) > 0 && o.BaseId is >= 1 and <= GameMemory.GateLast), 0.98, tie: true);
        Add(Standing, L.Checks.Ruler, data.Forces.Values.Where(f => f.RulerId is not (0 or 0xFFFF))
            .Select(f => Officer(f.RulerId) is { Status: 1 } ruler && data.ForceOfCorps(ruler.Corps) == f.Id), 0.9, tie: true);
        Add(Standing, L.Checks.FreeHaveNoCorps, active.Where(o => o.Status == 5).Select(o => o.Corps == 0), 0.95, tie: true);
        var leading = data.Units.Select(u => u.Commander).ToHashSet();
        // (Many are out on the way or on tasks: in a war in 211 only 35% were home; read from the wrong place, hardly any.)
        Add(Standing, L.Checks.InsideBase, members.Where(o => !leading.Contains(o.Id)).Select(o => o[OfficerFields.PlaceField] == o.BaseId), 0.2);

        // 세력: 친선 with itself is 100; the 군사 and the officers in 정책 slots are its own.
        ForceRecord[] forces = data.ForceRecords.Values.Where(f => f.Ruler is not (0 or 0xFFFF) && f.Id <= GameMemory.MaxPlayerForce).ToArray();
        Add(Force, L.Checks.OwnFriendship, forces.Select(f => f[ForceFields.Friendship(f.Id)] == 100));
        Add(Force, L.Checks.ForceRanges, forces.Select(f => f[ForceFields.Rank] is >= 1 and <= 12 && f[ForceFields.Color] <= 60 && f[ForceFields.Country] <= 100));
        Add(Force, L.Checks.Strategist, forces.Where(f => f[ForceFields.Strategist] != 0)
            .Select(f => Officer(f[ForceFields.Strategist]) is { } o && OfficerFields.IsMember(o.Status) && data.ForceOfCorps(o.Corps) == f.Id), 0.9, tie: true);
        Add(Force, L.Checks.PolicySlots, forces.SelectMany(f => Enumerable.Range(0, ForceFields.PolicyFields.Length)
            .SelectMany(field => Enumerable.Range(0, ForceFields.PolicySlots).Select(slot => (Force: f.Id, Officer: f[ForceFields.PolicyOfficer(field, slot)]))))
            .Where(s => s.Officer != 0).Select(s => Officer(s.Officer) is { } o && data.ForceOfCorps(o.Corps) == s.Force), 0.9, tie: true);

        // 명품: a found one is held by an officer.
        Add(Item, L.Checks.FoundItemOwner, data.Items.Where(i => i.State == ItemFields.Found && i.Owner is > 0 and < 3000).Select(i => Officer(i.Owner) is not null), 0.95, tie: true);

        Add(Item, L.Checks.ItemAbility, data.Items.Select(i => i.Ability <= 5 && i.Bonus <= 100 && (i.Ability != 0 || i.Bonus == 0)), 0.98);

        // 개성: effects of known kinds, and no value without a kind.
        Add(Personality, L.Checks.PersonalityEffects, data.Personalities.Select(p => Enumerable.Range(0, 2).All(e => p.Kind(e) <= 300 && (p.Kind(e) != 0 || p.Value(e) == 0))), 0.98);

        // 부대: led by an officer, troops and morale in range.
        Add(Unit, L.Checks.Units, data.Units.Select(u => Officer(u.Commander) is not null &&
            u[UnitFields.Troops] is >= 1 and <= 60_000 && u[UnitFields.Morale] <= 150), 0.98);

        // 플레이 기록: tutorials within the game's 20, 고대무장 bits only where officers have them (and bit 0, which
        // both records read on 2026-10-03 held), 전기제패 ranks 0 (S) to 4.
        if (data.Play is { } play)
        {
            ulong known = Enumerable.Range(AncientOfficers.First, AncientOfficers.Last - AncientOfficers.First + 1)
                .Select(AncientOfficers.Bit).OfType<int>().Aggregate(1UL, (bits, bit) => bits | 1UL << bit);
            Add(PlayRecord, L.Checks.PlayRecordRanges, [play.Tutorials >> 20 == 0, (play.Unlocks & ~known) == 0,
                !play.Ranks.ContainsAnyExceptInRange((byte)0, (byte)PlayRecordFields.NoRank)]);
        }

        // 게임 상태 (AUTO(데모) 플레이 중지, 진행 여부): the date reads as a date, the hour as an hour.
        Add(World, L.Checks.GameDate, [data.Date is not null && data.Hour is >= 0 and < 24]);
        return checks;
    }

    // The facts tying records together that a write would break: those it leaves with more records missing them
    // than before (a game may already hold an odd record or two; those are not the write's).
    internal static List<string> Broken(GameSnapshot data, IReadOnlyList<RecordEdit> edits, GameCatalog catalog)
    {
        List<Check> before = Run(data, catalog), after = Run(data.With(edits), catalog);
        return before.Zip(after).Where(pair => pair.First.Tie && pair.Second.Misses > pair.First.Misses)
            .Select(pair => L.Checks.MissesChanged(pair.First.What, pair.First.Misses, pair.Second.Misses)).ToList();
    }

    // Why a write may not go in while the game runs, or null when it may: one that changes a linked field (whom an
    // officer serves, a 태수, a spouse, an office, a 정책 slot, a 명품's holder) is planned from the game as read
    // and changes several records one after another, which the game could act on half done; it waits for [정지].
    // Plain values (금, 충성, 능력) go in while it runs.
    internal static string? PaceBlock(GamePace pace, IEnumerable<FieldChange> changes) =>
        pace == GamePace.Running && changes.Any(c => LinkedFields.Contains(c.Field))
            ? L.Checks.GameRunning
            : null;

    // Why values of these groups may not be written to this build now, or null when they may: an unverified build
    // is read-only until the user allows writing to it, and then takes only the groups that passed.
    internal static string? WriteBlock(bool verified, bool allowed, IReadOnlyList<Check> checks, IEnumerable<string> groups)
    {
        if (verified) return null;
        if (!allowed) return L.Checks.ReadOnly;
        string[] failed = groups.Distinct().Where(g => checks.Any(c => c.Group == g && !c.Passed)).ToArray();
        return failed.Length == 0 ? null : L.Checks.Failed(string.Join(", ", failed));
    }
}
