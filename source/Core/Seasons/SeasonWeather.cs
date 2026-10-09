using System.Numerics;

namespace San14Tenkazu.Seasons;

internal enum SeasonDepthDirection { Unverified=0, Forward=1, Reversed=2 }
internal readonly record struct SeasonHazeParameters(float Intensity,float AmplitudePixels,
    float PhaseRadians,Vector2 Frequency);

// Artwork math only. Camera/region/depth resource and pre-UI pass selection belong to a verified caller.
internal static class SeasonWeather
{
    internal static Vector2 HazeOffsetPixels(Vector2 uv,SeasonHazeParameters parameters)
    {
        if(!Unit(uv.X)||!Unit(uv.Y)||!float.IsFinite(parameters.Intensity)||parameters.Intensity is <0 or >.25f||
            !float.IsFinite(parameters.AmplitudePixels)||parameters.AmplitudePixels is <0 or >8||
            !float.IsFinite(parameters.PhaseRadians)||parameters.PhaseRadians is <0 or >7||
            !float.IsFinite(parameters.Frequency.X)||!float.IsFinite(parameters.Frequency.Y)||
            parameters.Frequency.X is <0 or >64||parameters.Frequency.Y is <0 or >64)
            throw new ArgumentOutOfRangeException(nameof(parameters));
        float scale=parameters.Intensity*parameters.AmplitudePixels;
        return new(MathF.Sin(uv.Y*parameters.Frequency.X+parameters.PhaseRadians)*scale,
            MathF.Sin(uv.X*parameters.Frequency.Y+parameters.PhaseRadians)*scale);
    }
    internal static bool SnowVisible(float sceneDepth,float particleDepth,SeasonDepthDirection direction)
    {
        if(!Unit(sceneDepth)||!Unit(particleDepth))throw new ArgumentOutOfRangeException(nameof(sceneDepth));
        return direction switch
        {
            SeasonDepthDirection.Forward=>particleDepth<=sceneDepth,
            SeasonDepthDirection.Reversed=>particleDepth>=sceneDepth,
            _=>throw new InvalidDataException("Verified projection/depth direction is required")
        };
    }
    internal static float SnowAlpha(Vector2 flake,float intensity,float opacity)
    {
        if(!float.IsFinite(flake.X)||!float.IsFinite(flake.Y)||flake.X is < -1 or >1||flake.Y is < -1 or >1||
            !float.IsFinite(intensity)||intensity is <0 or >.35f||!Unit(opacity))
            throw new ArgumentOutOfRangeException(nameof(intensity));
        float t=Math.Clamp((Vector2.Dot(flake,flake)-.25f)/.75f,0,1);
        return (1-t*t*(3-2*t))*intensity*opacity;
    }
    // Periodic motion in an explicitly authored unit volume. No game up axis or camera is inferred.
    // Travel may use any axis; phase comes from a bounded monotonic visual clock, never a game-date write.
    internal static Vector3 SnowPosition(Vector3 initialFraction,Vector3 travelPerCycle,double phase)
    {
        if(!Unit(initialFraction.X)||!Unit(initialFraction.Y)||!Unit(initialFraction.Z)||
            !float.IsFinite(travelPerCycle.X)||!float.IsFinite(travelPerCycle.Y)||!float.IsFinite(travelPerCycle.Z)||
            MathF.Abs(travelPerCycle.X)>8||MathF.Abs(travelPerCycle.Y)>8||MathF.Abs(travelPerCycle.Z)>8||
            !double.IsFinite(phase)||phase is <0 or >=1)
            throw new ArgumentOutOfRangeException(nameof(phase));
        static float Wrap(double value)=>(float)(value-Math.Floor(value));
        return new(Wrap(initialFraction.X+travelPerCycle.X*phase),Wrap(initialFraction.Y+travelPerCycle.Y*phase),
            Wrap(initialFraction.Z+travelPerCycle.Z*phase));
    }
    private static bool Unit(float value)=>float.IsFinite(value)&&value is >=0 and <=1;
}
