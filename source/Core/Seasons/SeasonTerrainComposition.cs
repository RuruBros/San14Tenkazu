using System.Numerics;

namespace San14Tenkazu.Seasons;

// Linear-light material inputs in one caller-verified tangent basis. No game binding/UV inference.
internal readonly record struct SeasonTerrainMaterial(Vector3 Albedo, Vector3 Normal, float Roughness);
internal readonly record struct SeasonTerrainInfluence(Vector4 Ground, float Snow)
{
    internal static SeasonTerrainInfluence FromClimate(SeasonIntensity intensity) => new(
        new((float)intensity.NewLeaves, (float)intensity.Foliage, (float)intensity.Autumn,
            (float)(1-intensity.LeafRetention)), (float)intensity.SnowCover);
}
internal readonly record struct SeasonTerrainParameters(float GroundCoverage, float SnowEligibility,
    float UpFacing, float GroundStrength, float SnowStrength, float SnowSlopeMin, float SnowSlopeMax);
internal readonly record struct SeasonTerrainResult(SeasonTerrainMaterial Material, Vector4 GroundWeights,
    float SnowWeight);

// Reference implementation of our artwork compositor. Alpha, geometry, lighting and depth are caller-owned.
internal static class SeasonTerrainComposition
{
    internal static SeasonTerrainResult Compose(SeasonTerrainMaterial original,
        IReadOnlyList<SeasonTerrainMaterial> layers, SeasonTerrainInfluence influence,
        SeasonTerrainParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(layers);
        if(layers.Count!=5)throw new ArgumentException("Spring, summer, autumn, winter litter and snow are required",nameof(layers));
        Validate(original);foreach(var layer in layers)Validate(layer);
        var ground=influence.Ground;
        foreach(float value in new[]{ground.X,ground.Y,ground.Z,ground.W,influence.Snow,
            parameters.GroundCoverage,parameters.SnowEligibility,parameters.UpFacing,parameters.GroundStrength,
            parameters.SnowStrength,parameters.SnowSlopeMin,parameters.SnowSlopeMax})
            if(!Unit(value))throw new ArgumentOutOfRangeException(nameof(parameters),"Finite unit-range artwork inputs are required");
        float total=ground.X+ground.Y+ground.Z+ground.W;
        if(total>1+1e-6f||parameters.SnowSlopeMax-parameters.SnowSlopeMin<0.0001f)
            throw new ArgumentOutOfRangeException(nameof(parameters),"Normalized ground contributions and a snow slope interval of at least 1e-4 are required");
        // Only absorb floating-point roundoff in valid climate contributions; invalid sums are rejected above.
        ground*=parameters.GroundCoverage*parameters.GroundStrength/MathF.Max(1,total);
        float fraction=Math.Clamp((parameters.UpFacing-parameters.SnowSlopeMin)/
            (parameters.SnowSlopeMax-parameters.SnowSlopeMin),0,1);
        float snow=influence.Snow*parameters.SnowEligibility*parameters.SnowStrength*fraction*fraction*(3-2*fraction);
        float remaining=1-(ground.X+ground.Y+ground.Z+ground.W);
        if(remaining==1&&snow==0)return new(original,ground,snow);
        Vector3 color=original.Albedo*remaining,normal=original.Normal*remaining;
        float roughness=original.Roughness*remaining;
        for(int i=0;i<4;i++)
        {
            float weight=ground[i];color+=layers[i].Albedo*weight;normal+=layers[i].Normal*weight;
            roughness+=layers[i].Roughness*weight;
        }
        color=color*(1-snow)+layers[4].Albedo*snow;
        normal=normal*(1-snow)+layers[4].Normal*snow;
        roughness=roughness*(1-snow)+layers[4].Roughness*snow;
        // Antipodal tangent normals have no normalized mean. Keep the validated original direction.
        float squared=Vector3.Dot(normal,normal);
        normal=squared>1e-12f?normal/MathF.Sqrt(squared):original.Normal;
        return new(new(color,normal,roughness),ground,snow);
    }
    private static bool Unit(float value)=>float.IsFinite(value)&&value is >=0 and <=1;
    private static void Validate(SeasonTerrainMaterial sample)
    {
        float length=Vector3.Dot(sample.Normal,sample.Normal);
        if(!Unit(sample.Albedo.X)||!Unit(sample.Albedo.Y)||!Unit(sample.Albedo.Z)||!Unit(sample.Roughness)||
            !float.IsFinite(length)||MathF.Abs(length-1)>0.0001f)
            throw new ArgumentException("Linear unit-range color/roughness and a finite unit tangent normal are required");
    }
}
