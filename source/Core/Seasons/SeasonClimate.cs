using System.Text.Json;

namespace San14Tenkazu.Seasons;

internal readonly record struct MonthlyClimate(double TemperatureC, double PrecipitationMm);
internal readonly record struct SeasonIntensity(double NewLeaves, double Foliage, double Autumn,
    double LeafRetention, double SnowCover, double Snowfall, double HeatHaze);

internal sealed class ClimateStation
{
    internal int Id { get; }
    internal int CityId { get; }
    internal double X { get; }
    internal double Z { get; }
    private readonly double[] temperatures, precipitation;
    internal ClimateStation(int id, int cityId, double x, double z, double[] temperatures, double[] precipitation)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) throw new ArgumentOutOfRangeException(nameof(x));
        if (temperatures.Length!=12 || precipitation.Length!=12 ||
            temperatures.Any(v=> !double.IsFinite(v) || v is < -60 or > 60) ||
            precipitation.Any(v=> !double.IsFinite(v) || v is < 0 or > 3000))
            throw new ArgumentException("Complete finite JMA monthly normals are required");
        (Id,CityId,X,Z)=(id,cityId,x,z);
        this.temperatures=(double[])temperatures.Clone(); this.precipitation=(double[])precipitation.Clone();
    }
    internal MonthlyClimate Sample(MonthBlend date) => new(date.Sample(temperatures),date.Sample(precipitation));
}

internal sealed class SeasonClimate
{
    private readonly ClimateStation[] stations;
    internal IReadOnlyList<ClimateStation> Stations => Array.AsReadOnly(stations);
    internal SeasonClimate(IEnumerable<ClimateStation> stations)
    {
        this.stations=stations.ToArray();
        if (this.stations.Length==0 || this.stations.Select(s=>s.Id).Distinct().Count()!=this.stations.Length)
            throw new ArgumentException("Distinct climate stations are required", nameof(stations));
    }
    internal static SeasonClimate Load()
    {
        using var stream=typeof(SeasonClimate).Assembly.GetManifestResourceStream("Seasons.climate.json")
            ?? throw new InvalidDataException("Season climate resource is missing");
        return Load(stream);
    }
    internal static SeasonClimate Load(Stream stream)
    {
        using var json=JsonDocument.Parse(stream);
        var list=new List<ClimateStation>();
        foreach (var s in json.RootElement.GetProperty("stations").EnumerateArray())
            list.Add(new(s.GetProperty("stationId").GetInt32(),s.GetProperty("cityId").GetInt32(),
                s.GetProperty("gameX").GetDouble(),s.GetProperty("gameZ").GetDouble(),
                s.GetProperty("temperatureC").EnumerateArray().Select(v=>v.GetDouble()).ToArray(),
                s.GetProperty("precipitationMm").EnumerateArray().Select(v=>v.GetDouble()).ToArray()));
        return new(list);
    }
    internal MonthlyClimate Sample(double x, double z, MonthBlend date)
    {
        if (!double.IsFinite(x) || !double.IsFinite(z)) throw new ArgumentOutOfRangeException(nameof(x));
        // All verified city anchors, inverse squared distance. Keeping a fixed anchor set avoids
        // jumps where the former fourth-nearest anchor changes. Exact anchors retain their source normal.
        var nearest=stations.Select(s=>(Station:s,Distance:(s.X-x)*(s.X-x)+(s.Z-z)*(s.Z-z)))
            .OrderBy(s=>s.Distance).ThenBy(s=>s.Station.Id).ToArray();
        if (nearest.Any(s=> !double.IsFinite(s.Distance))) throw new ArgumentOutOfRangeException(nameof(x),"Climate distance exceeds the numeric range");
        if (nearest[0].Distance<1e-12) return nearest[0].Station.Sample(date);
        double temp=0,rain=0,weight=0;
        foreach (var item in nearest)
        {
            double w=1/item.Distance;var normal=item.Station.Sample(date);
            temp+=normal.TemperatureC*w;rain+=normal.PrecipitationMm*w;weight+=w;
        }
        return new(temp/weight,rain/weight);
    }
    // Explicit visual response curves; these are artwork parameters, not meteorological predictions.
    // They use the original modern normals without a historical temperature offset.
    internal static SeasonIntensity Intensity(MonthlyClimate climate, SeasonWeights season)
    {
        if (!season.IsValid) throw new ArgumentOutOfRangeException(nameof(season));
        if (!double.IsFinite(climate.TemperatureC) || !double.IsFinite(climate.PrecipitationMm) || climate.PrecipitationMm<0)
            throw new ArgumentOutOfRangeException(nameof(climate));
        double cold=1-Smooth(0,8,climate.TemperatureC);
        double moisture=Math.Clamp(climate.PrecipitationMm/40,0,1);
        double retain=1-season.Winter*(1-Smooth(4,14,climate.TemperatureC));
        return new(season.Spring*Smooth(4,18,climate.TemperatureC),season.Summer,
            season.Autumn,retain,season.Winter*cold*(0.3+0.7*moisture),
            season.Winter*cold*moisture*0.35,0);
    }
    private static double Smooth(double min,double max,double value)
    {
        double t=Math.Clamp((value-min)/(max-min),0,1);return t*t*(3-2*t);
    }
}
