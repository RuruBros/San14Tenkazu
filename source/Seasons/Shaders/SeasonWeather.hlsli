// Shared own artwork functions. Scene selection, region strength, camera and raw depth direction are verified by the caller.
float2 SeasonHazeOffsetPixels(float2 uv,float intensity,float amplitudePixels,float phase,float2 frequency)
{
    return sin(float2(uv.y*frequency.x,uv.x*frequency.y)+phase)*intensity*amplitudePixels;
}
float SeasonSnowAlpha(float2 flake,float intensity,float opacity)
{
    float t=saturate((dot(flake,flake)-.25)/.75);
    return (1-t*t*(3-2*t))*intensity*opacity;
}
bool SeasonSnowVisible(float sceneDepth,float particleDepth,uint direction)
{
    // Host rejects these inputs too; a malformed depth must never create particle coverage.
    if(!isfinite(sceneDepth)||!isfinite(particleDepth)||sceneDepth<0||sceneDepth>1||particleDepth<0||particleDepth>1)return false;
    if(direction==1)return particleDepth<=sceneDepth;
    if(direction==2)return particleDepth>=sceneDepth;
    return false;
}
