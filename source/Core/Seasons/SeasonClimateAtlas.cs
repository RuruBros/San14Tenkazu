namespace San14Tenkazu.Seasons;

internal readonly record struct ClimateExtent(double MinX,double MinZ,double MaxX,double MaxZ)
{
    internal bool IsValid => double.IsFinite(MinX) && double.IsFinite(MinZ) &&
        double.IsFinite(MaxX) && double.IsFinite(MaxZ) && MaxX>MinX && MaxZ>MinZ &&
        double.IsFinite(MaxX-MinX) && double.IsFinite(MaxZ-MinZ);
}

// Immutable raw temperature/precipitation layers, month 1..12 stored at array slices 0..11.
// The atlas uses clamped bilinear sampling at LOD 0, independent of camera zoom.
internal sealed class SeasonClimateAtlas
{
    internal int Width { get; }
    internal int Height { get; }
    internal ClimateExtent Extent { get; }
    private readonly float[] data;
    internal ReadOnlyMemory<float> Data => data;
    private SeasonClimateAtlas(int width,int height,ClimateExtent extent,float[] data)
        => (Width,Height,Extent,this.data)=(width,height,extent,data);

    internal static SeasonClimateAtlas Build(SeasonClimate climate,int width,int height,ClimateExtent extent)
    {
        ArgumentNullException.ThrowIfNull(climate);
        if(width is <1 or >1024 || height is <1 or >1024 || !extent.IsValid)
            throw new ArgumentOutOfRangeException(nameof(extent));
        var data=new float[checked(width*height*12*2)];
        // Each texel represents its center; this matches D3D's normalized texture coordinates.
        Parallel.For(0,height,new ParallelOptions{MaxDegreeOfParallelism=2},y=>
        {
            Span<MonthlyClimate> months=stackalloc MonthlyClimate[12];
            for(int x=0;x<width;x++)
            {
                double gx=extent.MinX+(x+0.5)/width*(extent.MaxX-extent.MinX);
                double gz=extent.MinZ+(y+0.5)/height*(extent.MaxZ-extent.MinZ);
                climate.SampleMonths(gx,gz,months);
                for(int month=0;month<12;month++)
                {
                    int at=((month*height+y)*width+x)*2;
                    data[at]=(float)months[month].TemperatureC;data[at+1]=(float)months[month].PrecipitationMm;
                    if(!float.IsFinite(data[at]) || !float.IsFinite(data[at+1]))
                        throw new InvalidDataException("Nonfinite climate atlas value");
                }
            }
        });
        return new(width,height,extent,data);
    }
    internal MonthlyClimate Sample(double x,double z,MonthBlend date)
    {
        _=date.Weights; // Validate adjacent months and the interpolation fraction before indexing.
        if(!double.IsFinite(x) || !double.IsFinite(z))throw new ArgumentOutOfRangeException(nameof(x));
        // Clamp in game coordinates first, so even finite coordinates near double.MaxValue are safe.
        double px=(Math.Clamp(x,Extent.MinX,Extent.MaxX)-Extent.MinX)/(Extent.MaxX-Extent.MinX)*Width-0.5;
        double pz=(Math.Clamp(z,Extent.MinZ,Extent.MaxZ)-Extent.MinZ)/(Extent.MaxZ-Extent.MinZ)*Height-0.5;
        int ix=(int)Math.Floor(px),iz=(int)Math.Floor(pz);double fx=px-ix,fz=pz-iz;
        double Channel(int month,int channel)
        {
            double Texel(int tx,int tz)=>data[((month*Height+Math.Clamp(tz,0,Height-1))*Width+Math.Clamp(tx,0,Width-1))*2+channel];
            double top=Texel(ix,iz)+(Texel(ix+1,iz)-Texel(ix,iz))*fx;
            double bottom=Texel(ix,iz+1)+(Texel(ix+1,iz+1)-Texel(ix,iz+1))*fx;
            return top+(bottom-top)*fz;
        }
        double Interpolate(int c)
        {
            double a=Channel(date.Month-1,c),b=Channel(date.NextMonth-1,c);
            return a+(b-a)*date.Fraction;
        }
        return new(Interpolate(0),Interpolate(1));
    }
}
