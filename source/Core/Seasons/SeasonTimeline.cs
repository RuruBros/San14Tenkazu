namespace San14Tenkazu.Seasons;

internal readonly record struct SeasonDate
{
    internal int Year { get; }
    internal int Month { get; }
    internal int Day { get; }
    private SeasonDate(int year, int month, int day) => (Year, Month, Day) = (year, month, day);
    internal static bool TryCreate(int year, int month, int day, out SeasonDate date)
    {
        // The game's snapshot year guard and the verified NativeCalendar's 30-day month.
        bool valid = year is > 100 and < 400 && month is >= 1 and <= 12 && day is >= 1 and <= 30;
        date = valid ? new SeasonDate(year, month, day) : default;
        return valid;
    }
    internal bool IsValid => Year is > 100 and < 400 && Month is >= 1 and <= 12 && Day is >= 1 and <= 30;
}

internal readonly record struct SeasonWeights(double Spring, double Summer, double Autumn, double Winter)
{
    internal bool IsValid => double.IsFinite(Spring) && double.IsFinite(Summer) && double.IsFinite(Autumn) && double.IsFinite(Winter) &&
        Spring is >=0 and <=1 && Summer is >=0 and <=1 && Autumn is >=0 and <=1 && Winter is >=0 and <=1 &&
        Math.Abs(Spring+Summer+Autumn+Winter-1)<1e-12;
    internal static SeasonWeights Month(int month) => month switch
    {
        >= 3 and <= 5 => new(1, 0, 0, 0),
        >= 6 and <= 8 => new(0, 1, 0, 0),
        >= 9 and <= 11 => new(0, 0, 1, 0),
        12 or 1 or 2 => new(0, 0, 0, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(month))
    };
    internal static SeasonWeights Lerp(SeasonWeights a, SeasonWeights b, double t)
    {
        if (!a.IsValid || !b.IsValid || !double.IsFinite(t) || t is <0 or >1)
            throw new ArgumentOutOfRangeException(nameof(t),"Finite normalized season weights and interpolation are required");
        return new(a.Spring + (b.Spring-a.Spring)*t, a.Summer + (b.Summer-a.Summer)*t,
            a.Autumn + (b.Autumn-a.Autumn)*t, a.Winter + (b.Winter-a.Winter)*t);
    }
}

internal readonly record struct MonthBlend(int Month, int NextMonth, double Fraction)
{
    private void Validate()
    {
        if (Month is <1 or >12 || NextMonth!=(Month==12?1:Month+1) ||
            !double.IsFinite(Fraction) || Fraction is <0 or >1)
            throw new ArgumentOutOfRangeException(nameof(MonthBlend),"Adjacent calendar months and finite interpolation are required");
    }
    internal double Sample(IReadOnlyList<double> values)
    {
        Validate();ArgumentNullException.ThrowIfNull(values);
        if (values.Count != 12) throw new ArgumentException("Twelve monthly values are required", nameof(values));
        double a=values[Month-1], b=values[NextMonth-1];
        if (!double.IsFinite(a) || !double.IsFinite(b)) throw new ArgumentException("Finite monthly values are required",nameof(values));
        return a+(b-a)*Fraction;
    }
    internal SeasonWeights Weights
    {
        get { Validate();return SeasonWeights.Lerp(SeasonWeights.Month(Month), SeasonWeights.Month(NextMonth), Fraction); }
    }
}

internal static class SeasonTimeline
{
    // Monthly anchors occur on day 1. December's next anchor is January, including across years.
    // No accumulated elapsed time: a save load or backward date always samples its own calendar date.
    internal static MonthBlend At(SeasonDate date)
    {
        if (!date.IsValid) throw new ArgumentOutOfRangeException(nameof(date));
        return new(date.Month, date.Month==12 ? 1 : date.Month+1, (date.Day-1)/30.0);
    }
}
