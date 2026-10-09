#pragma once
#include <d3dcompiler.h>
#include <wrl/client.h>
#include <string>
#include <sstream>
#include <regex>
#include <vector>
#include <cstring>
#include <stdexcept>

// Restricted translator for the authenticated terrain PS. The original DXBC
// remains an installation asset. Unsupported instructions fail preparation;
// there is no guessed material table or replacement of the engine's lighting.
namespace seasons::shader {
inline std::string trim(std::string s){auto a=s.find_first_not_of(" \r\n\t"),b=s.find_last_not_of(" \r\n\t");return a==s.npos?"":s.substr(a,b-a+1);}
inline std::vector<std::string> arguments(const std::string& s){
    std::vector<std::string> out;size_t start=0;int depth=0;
    for(size_t i=0;i<s.size();i++){if(s[i]=='('||s[i]=='[')++depth;if(s[i]==')'||s[i]==']')--depth;
        if(s[i]==','&&!depth){out.push_back(trim(s.substr(start,i-start)));start=i+1;}}
    out.push_back(trim(s.substr(start)));return out;
}
inline std::string raw(std::string s){
    s=trim(s);
    if(s.starts_with("l(")){
        auto a=arguments(s.substr(2,s.size()-3));if(a.size()!=1&&a.size()!=4)throw std::runtime_error("Unsupported shader literal");
        std::string out="uint4(";for(int i=0;i<4;i++){
            auto v=a[a.size()==1?0:i];uint32_t bits;
            if(v.find('.')!=v.npos||v.find('e')!=v.npos){float f=std::stof(v);std::memcpy(&bits,&f,4);}else bits=static_cast<uint32_t>(std::stoll(v));
            out+=(i?",":"")+std::to_string(bits)+"u";
        }return out+")";
    }
    std::smatch m;
    if(std::regex_match(s,m,std::regex(R"((r\d+|v\d+|o\d+)\.([xyzw]{1,4}))"))){
        std::string sw=m[2];if(sw.size()==1)sw=std::string(4,sw[0]);if(sw.size()!=4)throw std::runtime_error("Unsupported source swizzle");
        return std::string(m[1])+"."+sw;
    }
    if(std::regex_match(s,m,std::regex(R"(cb0\[(\d+|r\d+\.[xyzw] \+ \d+)\]\.([xyzw]{1,4}))"))){
        std::string sw=m[2];if(sw.size()==1)sw=std::string(4,sw[0]);if(sw.size()!=4)throw std::runtime_error("Unsupported constant swizzle");
        return "GameConstants["+std::string(m[1])+"]."+sw;
    }
    throw std::runtime_error("Unsupported terrain operand: "+s);
}
inline std::string value(std::string s,char type='f'){
    bool negative=s.starts_with('-');if(negative)s=s.substr(1);
    bool absolute=s.starts_with('|')&&s.ends_with('|');if(absolute)s=s.substr(1,s.size()-2);
    auto r=raw(s);bool bitsModifier=type=='u'&&(negative||absolute);
    if(type=='f'||bitsModifier)r="asfloat("+r+")";else if(type=='i')r="asint("+r+")";
    if(absolute)r="abs("+r+")";if(negative)r="(-("+r+"))";
    return bitsModifier?"asuint("+r+")":r;
}
inline std::string assign(const std::string& destination,std::string expression,char type,bool saturate){
    std::smatch m;if(!std::regex_match(destination,m,std::regex(R"(([ro]\d+)\.([xyzw]{1,4}))")))throw std::runtime_error("Unsupported terrain destination");
    if(saturate)expression="saturate("+expression+")";
    if(type=='f'||type=='i')expression="asuint("+expression+")";
    return std::string(m[1])+"."+std::string(m[2])+"=("+expression+")."+std::string(m[2])+";\n";
}
inline std::string translate(const void* bytecode,size_t bytes,const std::string& seasonalInclude={},bool distant=false){
    Microsoft::WRL::ComPtr<ID3DBlob> assembly;
    if(FAILED(D3DDisassemble(bytecode,bytes,0,nullptr,&assembly)))throw std::runtime_error("Terrain DXBC disassembly failed");
    std::string listing(static_cast<const char*>(assembly->GetBufferPointer()));
    auto start=listing.find("ps_5_0");if(start==listing.npos)throw std::runtime_error("Expected terrain PS 5.0");
    std::istringstream input(listing.substr(start));std::string line,body,decl;
    const std::string shaderInterface=distant?R"(
#define SEASON_DISTANT_TERRAIN 1
cbuffer OriginalGame:register(b0){uint4 GameConstants[261];};
struct TerrainInput {float4 position:SV_Position;float4 a:TEXCOORD0;
 float3 tangent:TEXCOORD1;float3 normal:TEXCOORD2;float2 uv:TEXCOORD3;};
struct TerrainOutput {float4 a:SV_Target0;float4 n:SV_Target1;float4 r:SV_Target2;float4 z:SV_Target3;float4 b:SV_Target4;};
)":R"(
cbuffer OriginalGame:register(b0){uint4 GameConstants[1027];};
struct TerrainInput {float4 position:SV_Position;float4 a:TEXCOORD0;float4 b:TEXCOORD1;float4 c:TEXCOORD2;
 float4 world:TEXCOORD3;float3 tangent:TEXCOORD4;float3 normal:TEXCOORD5;float2 uv:TEXCOORD6;};
struct TerrainOutput {float4 a:SV_Target0;float4 n:SV_Target1;float4 r:SV_Target2;float4 z:SV_Target3;float4 b:SV_Target4;};
)";
    std::string locals=distant?"uint4 v1=asuint(input.a),v2=asuint(float4(input.tangent,0)),v3=asuint(float4(input.normal,0)),v4=asuint(float4(input.uv,0,0));\n":
        "uint4 v1=asuint(input.a),v2=asuint(input.b),v3=asuint(input.c),v4=asuint(input.world),v5=asuint(float4(input.tangent,0)),v6=asuint(float4(input.normal,0)),v7=asuint(float4(input.uv,0,0));\n";
    int nesting=0;bool returned=false;
    while(std::getline(input,line)){
        line=trim(line);if(line.empty()||line.starts_with("//")||line=="ps_5_0")continue;
        auto split=line.find(' ');std::string op=line.substr(0,split),rest=split==line.npos?"":trim(line.substr(split+1));
        if(op=="dcl_temps") {int n=std::stoi(rest);if(n!=(distant?6:32))throw std::runtime_error("Terrain temporary layout differs");for(int i=0;i<n;i++)locals+="uint4 r"+std::to_string(i)+"=0;\n";continue;}
        if(op=="dcl_sampler"){auto a=arguments(rest);decl+="SamplerState "+a[0]+":register("+a[0]+");\n";continue;}
        if(op.starts_with("dcl_resource_texture")){
            auto t=rest.substr(rest.find_last_of(' ')+1);decl+=(op=="dcl_resource_texture2darray"?"Texture2DArray<float4> ":"Texture2D<float4> ")+t+":register("+t+");\n";continue;
        }
        if(op.starts_with("dcl_"))continue;
        auto a=arguments(rest);auto f=[&](size_t i){return value(a.at(i));};auto u=[&](size_t i){return value(a.at(i),'u');};auto si=[&](size_t i){return value(a.at(i),'i');};
        if(op=="if_nz"){body+="if(("+u(0)+").x!=0){\n";++nesting;continue;}
        if(op=="else"){if(nesting<=0)throw std::runtime_error("Unbalanced shader else");body+="}else{\n";continue;}
        if(op=="endif"){if(--nesting<0)throw std::runtime_error("Unbalanced shader endif");body+="}\n";continue;}
        if(op=="ret"){if(nesting||returned)throw std::runtime_error("Unexpected terrain return");returned=true;continue;}
        if(op=="discard"||op=="discard_nz"){body+="if(("+u(0)+").x!=0)discard;\n";continue;}
        bool sat=op.ends_with("_sat");if(sat)op=op.substr(0,op.size()-4);
        std::string e;char type='f';
        if(op=="mov"){e=u(1);type='u';}
        else if(op=="add")e=f(1)+"+"+f(2);
        else if(op=="mul")e=f(1)+"*"+f(2);
        else if(op=="div")e=f(1)+"/"+f(2);
        else if(op=="mad")e="mad("+f(1)+","+f(2)+","+f(3)+")";
        else if(op=="max"||op=="min")e=op+"("+f(1)+","+f(2)+")";
        else if(op=="frc"||op=="sqrt"||op=="rsq")e=(op=="frc"?"frac":op=="rsq"?"rsqrt":"sqrt")+std::string("(")+f(1)+")";
        else if(op=="deriv_rtx_coarse"||op=="deriv_rty_coarse")e=(op=="deriv_rtx_coarse"?"ddx_coarse(":"ddy_coarse(")+f(1)+")";
        else if(op=="dp3")e="float4(dot(("+f(1)+").xyz,("+f(2)+").xyz),dot(("+f(1)+").xyz,("+f(2)+").xyz),dot(("+f(1)+").xyz,("+f(2)+").xyz),dot(("+f(1)+").xyz,("+f(2)+").xyz))";
        else if(op=="ftoi"){e="int4("+f(1)+")";type='i';}
        else if(op=="ftou"){e="uint4("+f(1)+")";type='u';}
        else if(op=="itof")e="float4("+si(1)+")";
        else if(op=="utof")e="float4("+u(1)+")";
        else if(op=="iadd"){e=si(1)+"+"+si(2);type='i';}
        else if(op=="and"||op=="ishl"){e=u(1)+(op=="and"?"&":"<<")+u(2);type='u';}
        else if(op=="eq"||op=="ge"||op=="lt"||op=="ieq"||op=="ige"){
            bool integer=op.starts_with('i');e="uint4("+(integer?si(1):f(1))+(op.ends_with("eq")||op=="eq"?"==":op.ends_with("ge")?">=":"<")+(integer?si(2):f(2))+")*0xffffffffu";type='u';
        }else if(op=="movc"){e="("+u(1)+"!=0?"+u(2)+":"+u(3)+")";type='u';}
        else if(op.starts_with("sample_")){
            auto dot=a.at(2).find('.');auto texture=a[2].substr(0,dot),sw=a[2].substr(dot+1);
            bool grad=op.starts_with("sample_d_"),array=op.find("texture2darray")!=op.npos;
            e=texture+(grad?".SampleGrad(":".Sample(")+a[3]+",("+f(1)+")."+(array?"xyz":"xy");
            if(grad)e+=",("+f(4)+").xy,("+f(5)+").xy";e+=")."+sw;
        }else throw std::runtime_error("Unsupported terrain instruction: "+op);
        body+=assign(a.at(0),e,type,sat);
    }
    if(!returned||nesting)throw std::runtime_error("Incomplete terrain program");
    locals+="uint4 o0=0,o1=0,o2=0,o3=0,o4=0;\n";
    std::string result=shaderInterface+decl+seasonalInclude+"\nTerrainOutput TerrainColor(TerrainInput input){\n"+locals+body+
        "TerrainOutput output;output.a=asfloat(o0);output.n=asfloat(o1);output.r=asfloat(o2);output.z=asfloat(o3);output.b=asfloat(o4);\n";
    if(!seasonalInclude.empty())result+="SeasonTerrainMaterial(input,output);\n";
    return result+"return output;}\n";
}
}
