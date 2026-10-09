#pragma once
#include <array>
#include <cstdint>
// Recorded metadata contracts, not assertions of actual PS bytecode identity.
namespace seasons::profile {
struct Signature {const char* name;uint32_t index,reg,system,component,mask,rw,stream;};
struct Resource {const char* name;uint32_t kind,slot,count,flags,result,dimension,samples;};
struct Profile {uint32_t version;std::array<uint32_t,36> counters;const Signature* inputs;const Signature* outputs;const Resource* resources;};
inline constexpr std::array<Signature,4> TreeColorinputs={{
    {"SV_Position",0u,0u,1u,3u,15u,0u,0u},
    {"TEXCOORD",0u,1u,0u,3u,15u,15u,0u},
    {"TEXCOORD",1u,2u,0u,3u,7u,7u,0u},
    {"TEXCOORD",2u,3u,0u,3u,3u,3u,0u},
}};
inline constexpr std::array<Signature,5> TreeColoroutputs={{
    {"SV_Target",0u,0u,64u,3u,15u,0u,0u},
    {"SV_Target",1u,1u,64u,3u,15u,0u,0u},
    {"SV_Target",2u,2u,64u,3u,15u,0u,0u},
    {"SV_Target",3u,3u,64u,3u,15u,0u,0u},
    {"SV_Target",4u,4u,64u,3u,15u,0u,0u},
}};
inline constexpr std::array<Resource,9> TreeColorresources={{
    {"__smpsStage0",3u,0u,1u,1u,0u,0u,0u},
    {"__smpsRefRough",3u,1u,1u,1u,0u,0u,0u},
    {"__smpsOcc",3u,2u,1u,1u,0u,0u,0u},
    {"__smpsNMap",3u,3u,1u,1u,0u,0u,0u},
    {"sStage0",2u,0u,1u,13u,5u,4u,4294967295u},
    {"sRefRough",2u,1u,1u,13u,5u,4u,4294967295u},
    {"sOcc",2u,2u,1u,13u,5u,4u,4294967295u},
    {"sNMap",2u,3u,1u,13u,5u,4u,4294967295u},
    {"$Globals",0u,0u,1u,0u,0u,0u,0u},
}};
inline constexpr Profile TreeColor={80,{256u,1u,9u,4u,5u,70u,6u,0u,0u,8u,4u,0u,0u,0u,0u,50u,1u,0u,1u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u},TreeColorinputs.data(),TreeColoroutputs.data(),TreeColorresources.data()};
inline constexpr std::array<Signature,2> TreeDepthinputs={{
    {"SV_Position",0u,0u,1u,3u,15u,0u,0u},
    {"TEXCOORD",0u,1u,0u,3u,3u,3u,0u},
}};
inline constexpr std::array<Signature,0> TreeDepthoutputs={{
}};
inline constexpr std::array<Resource,3> TreeDepthresources={{
    {"__smpsStage0",3u,0u,1u,1u,0u,0u,0u},
    {"sStage0",2u,0u,1u,13u,5u,4u,4294967295u},
    {"$Globals",0u,0u,1u,0u,0u,0u,0u},
}};
inline constexpr Profile TreeDepth={80,{256u,1u,3u,2u,0u,11u,1u,0u,0u,1u,1u,0u,0u,0u,0u,4u,0u,0u,2u,1u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u},TreeDepthinputs.data(),TreeDepthoutputs.data(),TreeDepthresources.data()};
inline constexpr std::array<Signature,8> Terraininputs={{
    {"SV_Position",0u,0u,1u,3u,15u,0u,0u},
    {"TEXCOORD",0u,1u,0u,3u,15u,12u,0u},
    {"TEXCOORD",1u,2u,0u,3u,15u,9u,0u},
    {"TEXCOORD",2u,3u,0u,3u,15u,5u,0u},
    {"TEXCOORD",3u,4u,0u,3u,15u,7u,0u},
    {"TEXCOORD",4u,5u,0u,3u,7u,7u,0u},
    {"TEXCOORD",5u,6u,0u,3u,7u,7u,0u},
    {"TEXCOORD",6u,7u,0u,3u,3u,3u,0u},
}};
inline constexpr std::array<Signature,5> Terrainoutputs={{
    {"SV_Target",0u,0u,64u,3u,15u,0u,0u},
    {"SV_Target",1u,1u,64u,3u,15u,0u,0u},
    {"SV_Target",2u,2u,64u,3u,15u,0u,0u},
    {"SV_Target",3u,3u,64u,3u,15u,0u,0u},
    {"SV_Target",4u,4u,64u,3u,15u,0u,0u},
}};
inline constexpr std::array<Resource,17> Terrainresources={{
    {"__smpsLandControl",3u,3u,1u,1u,0u,0u,0u},
    {"__smpsLandAlbedoOcclusion",3u,4u,1u,1u,0u,0u,0u},
    {"__smpsLandNormalHeight",3u,5u,1u,1u,0u,0u,0u},
    {"__smpsLandReflectanceLinearRoughness",3u,6u,1u,1u,0u,0u,0u},
    {"__smpsDetailLandAlbedoOcclusionArray",3u,7u,1u,1u,0u,0u,0u},
    {"__smpsDetailLandNormalHeightArray",3u,8u,1u,1u,0u,0u,0u},
    {"__smpsDetailLandReflectanceLinearRoughnessArray",3u,9u,1u,1u,0u,0u,0u},
    {"__smpsLandExAlbedo",3u,10u,1u,1u,0u,0u,0u},
    {"sLandControl",2u,3u,1u,13u,5u,4u,4294967295u},
    {"sLandAlbedoOcclusion",2u,4u,1u,13u,5u,4u,4294967295u},
    {"sLandNormalHeight",2u,5u,1u,13u,5u,4u,4294967295u},
    {"sLandReflectanceLinearRoughness",2u,6u,1u,13u,5u,4u,4294967295u},
    {"sDetailLandAlbedoOcclusionArray",2u,7u,1u,13u,5u,5u,4294967295u},
    {"sDetailLandNormalHeightArray",2u,8u,1u,13u,5u,5u,4294967295u},
    {"sDetailLandReflectanceLinearRoughnessArray",2u,9u,1u,13u,5u,5u,4294967295u},
    {"sLandExAlbedo",2u,10u,1u,13u,5u,4u,4294967295u},
    {"$Globals",0u,0u,1u,0u,0u,0u,0u},
}};
inline constexpr Profile Terrain={80,{256u,1u,17u,8u,5u,1300u,32u,0u,0u,12u,9u,0u,0u,0u,192u,632u,20u,50u,60u,61u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u},Terraininputs.data(),Terrainoutputs.data(),Terrainresources.data()};
inline constexpr std::array<Signature,2> Compositeinputs={{
    {"SV_Position",0u,0u,1u,3u,15u,3u,0u},
    {"TEXCOORD",0u,1u,0u,3u,3u,3u,0u},
}};
inline constexpr std::array<Signature,1> Compositeoutputs={{
    {"SV_Target",0u,0u,64u,3u,15u,0u,0u},
}};
inline constexpr std::array<Resource,3> Compositeresources={{
    {"sampleLinear",3u,7u,1u,1u,0u,0u,0u},
    {"g_tSceneMap",2u,0u,1u,13u,5u,4u,4294967295u},
    {"cbComposite",0u,2u,1u,1u,0u,0u,0u},
}};
inline constexpr Profile Composite={80,{256u,1u,3u,2u,1u,126u,6u,0u,0u,3u,0u,1u,0u,0u,0u,55u,4u,1u,3u,5u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u},Compositeinputs.data(),Compositeoutputs.data(),Compositeresources.data()};
// Distant terrain observed in PID 31080, zoomed-out map, recovered frame sequence 34.
inline constexpr std::array<Signature,5> DistantTerraininputs={{
    {"SV_Position",0u,0u,1u,3u,15u,0u,0u},
    {"TEXCOORD",0u,1u,0u,3u,15u,9u,0u},
    {"TEXCOORD",1u,2u,0u,3u,7u,7u,0u},
    {"TEXCOORD",2u,3u,0u,3u,7u,7u,0u},
    {"TEXCOORD",3u,4u,0u,3u,3u,3u,0u},
}};
inline constexpr std::array<Signature,5> DistantTerrainoutputs={{
    {"SV_Target",0u,0u,64u,3u,15u,0u,0u},
    {"SV_Target",1u,1u,64u,3u,15u,0u,0u},
    {"SV_Target",2u,2u,64u,3u,15u,0u,0u},
    {"SV_Target",3u,3u,64u,3u,15u,0u,0u},
    {"SV_Target",4u,4u,64u,3u,15u,0u,0u},
}};
inline constexpr std::array<Resource,11> DistantTerrainresources={{
    {"__smpsLandControl",3u,3u,1u,1u,0u,0u,0u},
    {"__smpsLandAlbedoOcclusion",3u,4u,1u,1u,0u,0u,0u},
    {"__smpsLandNormalHeight",3u,5u,1u,1u,0u,0u,0u},
    {"__smpsLandReflectanceLinearRoughness",3u,6u,1u,1u,0u,0u,0u},
    {"__smpsLandExAlbedo",3u,10u,1u,1u,0u,0u,0u},
    {"sLandControl",2u,3u,1u,13u,5u,4u,4294967295u},
    {"sLandAlbedoOcclusion",2u,4u,1u,13u,5u,4u,4294967295u},
    {"sLandNormalHeight",2u,5u,1u,13u,5u,4u,4294967295u},
    {"sLandReflectanceLinearRoughness",2u,6u,1u,13u,5u,4u,4294967295u},
    {"sLandExAlbedo",2u,10u,1u,13u,5u,4u,4294967295u},
    {"$Globals",0u,0u,1u,0u,0u,0u,0u},
}};
inline constexpr Profile DistantTerrain={80,{256u,1u,11u,5u,5u,96u,6u,0u,0u,9u,5u,0u,0u,0u,0u,60u,4u,2u,1u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u,0u},DistantTerraininputs.data(),DistantTerrainoutputs.data(),DistantTerrainresources.data()};
}
