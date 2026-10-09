#ifndef SEASON_VEGETATION
#define SEASON_VEGETATION
// Authored vegetation policy for the hash-authenticated 19-layer terrain.
// Layer artwork was inspected; these values are not automatic land-use labels.
uint SeasonPlantKind(int layer)
{
    if(layer==5||layer==17)return 1;
    if(layer==0||layer==7||layer==9||layer==12)return 2;
    return 0;
}
float SeasonCropInterior(float3 color)
{
    return color.g>color.b*1.45&&color.g>color.r*.82&&color.r>color.b*1.15?1:0;
}
float SeasonGreenInterior(float3 color)
{
    return color.g>color.b*1.35&&color.g>color.r*.8?1:0;
}
float SeasonSummerWind(float2 worldXZ,float seconds)
{
    // World-anchored gusts bend nearby clumps coherently. The broad component
    // survives minification as canopy lighting, rather than enlarged stalks.
    return sin(dot(worldXZ,float2(.00072,.00046))-seconds*1.15)*.65+
        sin(dot(worldXZ,float2(.0023,-.0014))-seconds*1.73)*.35;
}
float SeasonSummerCanopyLight(float2 worldXZ,float seconds)
{
    float broad=dot(worldXZ,float2(.00072,.00046))-seconds*1.15;
    float fine=dot(worldXZ,float2(.0023,-.0014))-seconds*1.73;
    // Derivative filtering integrates each sinusoid across the pixel footprint.
    float2 footprint=float2(fwidth(broad),fwidth(fine));
    float2 attenuation=saturate(1-footprint/6.2831853);
    return sin(broad)*.65*attenuation.x+sin(fine)*.35*attenuation.y;
}
#endif
