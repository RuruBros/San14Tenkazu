// Read-only reflection inspection on the game's frame. No GPU state changes.
#include <d3d11.h>
#include <d3d11shader.h>
#include <stdint.h>
struct ShaderCandidate { uint64_t owner, ownerVtable, pixelShader, reflection; };
struct ShaderInput { uint32_t count, reserved; ShaderCandidate candidates[128]; };
struct ShaderRecord {
    HRESULT result;
    uint32_t valid;
    D3D11_SHADER_DESC desc;
    D3D11_SIGNATURE_PARAMETER_DESC inputs[16], outputs[8];
    D3D11_SHADER_INPUT_BIND_DESC resources[32];
};
struct ShaderOutput { uint32_t count, complete; ShaderRecord records[128]; };
extern "C" __declspec(noinline) int season_inspect_shaders(ShaderInput* in, ShaderOutput* out) {
    if(!in || !out)return -3;
    out->complete=0;out->count=0;
    if(in->count>128)return -1;
    GUID iid;
    iid.Data1=0x8d536ca1;iid.Data2=0x0cca;iid.Data3=0x4956;
    iid.Data4[0]=0xa8;iid.Data4[1]=0x37;iid.Data4[2]=0x78;iid.Data4[3]=0x69;
    iid.Data4[4]=0x63;iid.Data4[5]=0x75;iid.Data4[6]=0x55;iid.Data4[7]=0x84;
    for(uint32_t i=0;i<in->count;++i) {
        ShaderCandidate* c=&in->candidates[i];ShaderRecord* r=&out->records[i];
        if(!c->owner || !c->ownerVtable || !c->pixelShader || !c->reflection ||
           *reinterpret_cast<uint64_t*>(c->owner)!=c->ownerVtable ||
           *reinterpret_cast<uint64_t*>(c->owner+40)!=c->pixelShader ||
           *reinterpret_cast<uint64_t*>(c->owner+16)!=c->reflection)return -2;
        ID3D11ShaderReflection* reflection=nullptr;
        r->result=reinterpret_cast<IUnknown*>(c->reflection)->QueryInterface(iid,reinterpret_cast<void**>(&reflection));
        r->valid=0;
        if(SUCCEEDED(r->result) && reflection) {
            r->result=reflection->GetDesc(&r->desc);
            if(SUCCEEDED(r->result) && r->desc.InputParameters<=16 && r->desc.OutputParameters<=8 && r->desc.BoundResources<=32) {
                r->valid=1;
                for(UINT j=0;j<r->desc.InputParameters && r->valid;++j) {
                    r->result=reflection->GetInputParameterDesc(j,&r->inputs[j]);
                    if(FAILED(r->result))r->valid=0;
                }
                for(UINT j=0;j<r->desc.OutputParameters && r->valid;++j) {
                    r->result=reflection->GetOutputParameterDesc(j,&r->outputs[j]);
                    if(FAILED(r->result))r->valid=0;
                }
                for(UINT j=0;j<r->desc.BoundResources && r->valid;++j) {
                    r->result=reflection->GetResourceBindingDesc(j,&r->resources[j]);
                    if(FAILED(r->result))r->valid=0;
                }
            }
            reflection->Release();
        }
        ++out->count;
    }
    out->complete=1;return 0;
}
static_assert(sizeof(D3D11_SHADER_DESC)==160,"Shader reflection ABI");
static_assert(sizeof(D3D11_SIGNATURE_PARAMETER_DESC)==40,"Shader reflection ABI");
static_assert(sizeof(D3D11_SHADER_INPUT_BIND_DESC)==40,"Shader reflection ABI");
static_assert(offsetof(ShaderRecord,inputs)==168,"Shader reflection ABI");
static_assert(offsetof(ShaderRecord,outputs)==808,"Shader reflection ABI");
static_assert(offsetof(ShaderRecord,resources)==1128,"Shader reflection ABI");
static_assert(sizeof(ShaderRecord)==2408,"Shader reflection ABI");
static_assert(sizeof(ShaderInput)==4104,"Shader reflection ABI");
static_assert(sizeof(ShaderOutput)==308232,"Shader reflection ABI");
