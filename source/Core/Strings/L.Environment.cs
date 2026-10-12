using System.Runtime.CompilerServices;
namespace San14Tenkazu;
internal static partial class L
{
    internal static class Environment
    {
        private static string T([CallerMemberName]string key="")=>Get(nameof(Environment)+"."+key);
        internal static string Menu=>T();internal static string MenuTip=>T();internal static string Visual=>T();
        internal static string VisualHint=>T();internal static string Busy=>T();
        internal static string WeatherVisual=>T();internal static string RainVisual=>T();internal static string SnowVisual=>T();internal static string WeatherVisualHint=>T();
        internal static string Weather=>T();internal static string Disasters=>T();internal static string Rain=>T();
        internal static string Snow=>T();internal static string Morale=>T();internal static string Flood=>T();
        internal static string Wildfire=>T();internal static string Drought=>T();internal static string Blizzard=>T();internal static string Heatwave=>T();
        internal static string Apply=>T();internal static string DisableAll=>T();internal static string Close=>T();
        internal static string Rules=>T();internal static string History=>T();internal static string NoEvents=>T();
        internal static string Independent=>T();internal static string Losses=>T();internal static string InvalidSettings=>T();
        internal static string SettingsMoved=>T();internal static string RegionUnavailable=>T();internal static string NotApplied=>T();
        internal static string Applying=>T();internal static string Off=>T();internal static string Saved=>T();
        internal static string SeasonStatus(object? status)=>Format(T(),(nameof(status),status));
        internal static string WeatherApplied(object? dated)=>Format(T(),(nameof(dated),dated));
        internal static string Active(object? flags,object? date)=>Format(T(),(nameof(flags),flags),(nameof(date),date));
        internal static string Failed(object? error)=>Format(T(),(nameof(error),error));
        internal static string RainRate=>T();internal static string HeavyRainRate=>T();internal static string SnowRate=>T();internal static string DeepSnowRate=>T();
        internal static string MoraleRate=>T();internal static string ChanceRate=>T();internal static string TroopsRate=>T();internal static string DisasterMoraleRate=>T();internal static string FoodRate=>T();
        internal static string Percent=>T();internal static string DecimalStep=>T();internal static string IntegerStep=>T();internal static string Summary=>T();
        internal static string Current=>T();
        internal static string CurrentWeather(object? date)=>Format(T(),(nameof(date),date));
        internal static string OverlayHint=>T();
        internal static string RecentDamage(object? date)=>Format(T(),(nameof(date),date));
        internal static string OverlayValues(object? movement,object? morale)=>Format(T(),(nameof(movement),movement),(nameof(morale),morale));
        internal static string NumberRange(object? label,object? max,object? step)=>Format(T(),(nameof(label),label),(nameof(max),max),(nameof(step),step));
        internal static string Event(object? date,object? kind,object? city,object? target,object? losses)=>Format(T(),(nameof(date),date),(nameof(kind),kind),(nameof(city),city),(nameof(target),target),(nameof(losses),losses));
        internal static string Unit(object? commander,object? tile)=>Format(T(),(nameof(commander),commander),(nameof(tile),tile));
        internal static string Damage(object? troops,object? morale,object? food)=>Format(T(),(nameof(troops),troops),(nameof(morale),morale),(nameof(food),food));
    }
}
