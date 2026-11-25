namespace San14Tenkazu;

internal enum BatchRevivalStatus { Free, Serving, Random }

// [일괄 부활]: which dead (or not yet appeared) officers to revive, and a drawn status, city and death year for each.
// Every drawn request is still planned and written one officer at a time by OfficerRevival and GameMemory.
internal static class OfficerBatchRevival
{
    internal const int DefaultMinYears = 20, DefaultMaxYears = 40, DefaultPick = 10;

    // City 0 draws a city for each officer; otherwise every officer goes to that city.
    internal sealed record Options(BatchRevivalStatus Status, int City, int MinYears, int MaxYears);

    // The officers the single revival would accept, and how many dead or hidden officers it would refuse.
    internal sealed record Candidates(IReadOnlyList<OfficerRecord> Officers, int Skipped);

    internal static Candidates Find(GameSnapshot data, bool includeHidden)
    {
        var commanders = data.Units.Select(u => u.Commander).ToHashSet();
        var officers = new List<OfficerRecord>();
        int skipped = 0;
        foreach (OfficerRecord officer in data.Officers)
        {
            if (officer.Status != 9 && !(includeHidden && officer.Status == 7)) continue;
            if (OfficerRevival.IsGenerated(officer) || commanders.Contains(officer.Id)) skipped++;
            else officers.Add(officer);
        }
        officers.Sort((a, b) => StringComparer.CurrentCulture.Compare(a.Name, b.Name) is var order and not 0 ? order : a.Id.CompareTo(b.Id));
        return new(officers, skipped);
    }

    // Cities only: a drawn gate would leave a 재야 officer standing in a pass.
    internal static IReadOnlyList<OfficerRevival.Destination> Cities(GameSnapshot data, RevivalMode mode) =>
        OfficerRevival.Targets(data, mode).Where(b => data.IsCity(b.Base)).ToArray();

    // A fixed city must take every status the draw can give: any city for 재야, a living ruler's city otherwise.
    internal static IReadOnlyList<OfficerRevival.Destination> CityChoices(GameSnapshot data, BatchRevivalStatus status) =>
        Cities(data, status == BatchRevivalStatus.Free ? RevivalMode.Free : RevivalMode.Serving);

    // Years after the current year a death year may be drawn from (the field's own limit).
    internal static int MaxYears(GameSnapshot data) => data.Date is { } date ? (int)OfficerFields.Death.Max - date.Year : 0;

    // Why these options cannot draw (null when they can).
    internal static string? Check(GameSnapshot data, Options options)
    {
        if (data.Date is null) return L.Transfers.NoDate;
        if (!Enum.IsDefined(options.Status)) return L.Revival.InvalidMode;
        int max = MaxYears(data);
        if (options.MinYears < 1 || options.MaxYears < options.MinYears || options.MaxYears > max)
            return L.BatchRevival.InvalidYears(max);
        if (options.City != 0)
            return CityChoices(data, options.Status).Any(c => c.Base == options.City) ? null : L.Transfers.DestinationInvalid;
        bool free = options.Status != BatchRevivalStatus.Serving, serving = options.Status != BatchRevivalStatus.Free;
        return free && Cities(data, RevivalMode.Free).Count == 0 || serving && Cities(data, RevivalMode.Serving).Count == 0
            ? L.BatchRevival.NoCities : null;
    }

    // A status, city and death year for each officer. The options must pass Check.
    internal static Dictionary<int, OfficerRevival.Request> Draw(GameSnapshot data, IEnumerable<int> officers, Options options, Random random)
    {
        if (Check(data, options) is { } why) throw new InvalidOperationException(why);
        int year = data.Date!.Value.Year;
        var cities = new Dictionary<RevivalMode, IReadOnlyList<OfficerRevival.Destination>>
        {
            [RevivalMode.Free] = Cities(data, RevivalMode.Free),
            [RevivalMode.Serving] = Cities(data, RevivalMode.Serving)
        };
        var draws = new Dictionary<int, OfficerRevival.Request>();
        foreach (int officer in officers)
        {
            RevivalMode mode = options.Status switch
            {
                BatchRevivalStatus.Free => RevivalMode.Free,
                BatchRevivalStatus.Serving => RevivalMode.Serving,
                _ => random.Next(2) == 0 ? RevivalMode.Free : RevivalMode.Serving
            };
            int city = options.City != 0 ? options.City : cities[mode][random.Next(cities[mode].Count)].Base;
            draws[officer] = new(officer, mode, city, year + random.Next(options.MinYears, options.MaxYears + 1));
        }
        return draws;
    }

    // Count officers drawn from all the candidates (every candidate once the count reaches them), in candidate order.
    internal static IReadOnlyList<int> Pick(IReadOnlyList<int> candidates, int count, Random random)
    {
        if (count >= candidates.Count) return [.. candidates];
        var shuffled = candidates.ToArray();
        random.Shuffle(shuffled);
        var picked = shuffled.Take(Math.Max(0, count)).ToHashSet();
        return candidates.Where(picked.Contains).ToArray();
    }
}
