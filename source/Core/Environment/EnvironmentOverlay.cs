namespace San14Tenkazu.EnvironmentRules;

// Current native receipt, never a fresh disaster roll or an inferred ongoing damage state.
internal sealed record EnvironmentOverlay(uint Date,EnvironmentEffects Flags,EnvironmentValues Values,byte[] Weather,uint[] RecentDates)
{
    internal int PreviewCity {get;init;}
    internal int PreviewKind {get;init;}
    internal static EnvironmentOverlay? From(EnvironmentStatus status)
    {
        if(status.Phase!=EnvironmentPhase.On||status.Receipt is not {} receipt)return null;
        var flags=status.Actual;uint[] dates=receipt.RecentDates.ToArray();bool preview=receipt.TestState==2;
        if(receipt.TestState is 2 or 4&&receipt.TestCity is >=1 and <=46&&receipt.TestKind<5){flags|=DisasterFlag((int)receipt.TestKind+3);if(preview)dates[(receipt.TestCity-1)*5+receipt.TestKind]=receipt.Date;}
        return flags==EnvironmentEffects.None?null:new(receipt.Date,flags,receipt.Values,receipt.Weather.ToArray(),dates){PreviewCity=preview?(int)receipt.TestCity:0,PreviewKind=(int)receipt.TestKind+3};
    }
    internal bool IsPreview(int city,int kind)=>PreviewCity==city&&PreviewKind==kind;
    internal int WeatherKind(int city)
    {
        if(city is <1 or >46||Weather.Length!=46)return 0;int w=Weather[city-1];
        if((w&3)!=0&&(Flags&(EnvironmentEffects.RainVisual|EnvironmentEffects.RainMovement|EnvironmentEffects.WeatherMorale))!=0)return 1;
        if((w&12)!=0&&(Flags&(EnvironmentEffects.SnowVisual|EnvironmentEffects.SnowMovement|EnvironmentEffects.WeatherMorale))!=0)return 2;
        return 0;
    }
    internal uint Recent(int city,int kind)
    {
        if(city is <1 or >46||kind is <3 or >7||RecentDates.Length!=230)return 0;
        if((Flags&DisasterFlag(kind))==0)return 0;uint date=RecentDates[(city-1)*5+kind-3];
        var now=EnvironmentProtocol.Date(Date);var then=EnvironmentProtocol.Date(date);
        if(!now.IsValid||!then.IsValid)return 0;long age=(long)EnvironmentPolicy.Serial(now)-EnvironmentPolicy.Serial(then);
        return age is >=0 and <10?date:0;
    }
    internal static EnvironmentEffects DisasterFlag(int kind)=>kind switch{3=>EnvironmentEffects.Flood,4=>EnvironmentEffects.Wildfire,5=>EnvironmentEffects.Drought,6=>EnvironmentEffects.Blizzard,7=>EnvironmentEffects.Heatwave,_=>EnvironmentEffects.None};
    internal int Shade(int city){for(int kind=3;kind<=7;kind++)if(Recent(city,kind)!=0)return kind;return WeatherKind(city);}
    internal IEnumerable<(int Kind,uint Date)> Marks(int city)
    {
        if(WeatherKind(city) is >0 and var weather)yield return(weather,Date);
        for(int kind=3;kind<=7;kind++)if(Recent(city,kind) is >0 and var date)yield return(kind,date);
    }
}
