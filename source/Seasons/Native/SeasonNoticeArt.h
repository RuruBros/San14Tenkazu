#pragma once
#include <fstream>
#include <filesystem>
#include <stdexcept>
// The game's original still layout supplies geometry, animation, tint and clipping.
// The installed stl_event_017 is G1T 0x5B (BC3), not BC7.
// Only its authenticated flood sprite's t0 binding is replaced for our owned report.
inline constexpr size_t NoticeArtBytes=32+4*1024*1024*4;
inline bool validNoticeArt(const BYTE* bytes,size_t count){
    const uint32_t expected[]{0x31415244,2,4,1024,1024,864,648,0};
    return count==NoticeArtBytes&&!std::memcmp(bytes,expected,sizeof(expected))&&
        materialSha(bytes,count,"a70e5ae892d6ed89e8e72f1698f37cdf6ed1421ebe05622e97da8b7dc1977430");
}
// Preserve the authenticated original sprite's irregular/feathered silhouette.
// BC3's first 8 bytes in each block contain its independent alpha palette/indices.
inline std::vector<BYTE> noticeBC3Alpha(const std::vector<BYTE>& source){
    if(source.size()!=1048576)throw std::invalid_argument("Original BC3 alpha size differs");
    std::vector<BYTE> alpha(1024*1024);
    for(UINT by=0;by<256;by++)for(UINT bx=0;bx<256;bx++){
        auto* block=source.data()+(size_t(by)*256+bx)*16;uint32_t a[8]{block[0],block[1]};
        if(a[0]>a[1])for(UINT i=1;i<=6;i++)a[i+1]=((7-i)*a[0]+i*a[1])/7;
        else {for(UINT i=1;i<=4;i++)a[i+1]=((5-i)*a[0]+i*a[1])/5;a[6]=0;a[7]=255;}
        uint64_t indices=0;for(UINT i=0;i<6;i++)indices|=uint64_t(block[2+i])<<(i*8);
        for(UINT y=0;y<4;y++)for(UINT x=0;x<4;x++)alpha[(size_t(by)*4+y)*1024+bx*4+x]=static_cast<BYTE>(a[(indices>>(3*(y*4+x)))&7]);
    }
    return alpha;
}
struct NoticeArtTicket {ComPtr<ID3D11ShaderResourceView> original;bool changed=false;};
class SeasonNoticeArt {
    const BYTE* pixels;
    std::wstring tracePath;
    std::array<std::atomic<uint64_t>,16> stages{};
    struct TraceTexture {uint64_t view;uint32_t viewFormat,dimension,firstMip,viewMips,width,height,textureFormat,mips,array,samples;bool accepted;std::string hash;};
    std::vector<TraceTexture> traceTextures;
    std::vector<BYTE> sourceAlpha;
    std::string digest(const std::vector<BYTE>& bytes){
        BCRYPT_ALG_HANDLE algorithm{};BCRYPT_HASH_HANDLE hash{};std::array<BYTE,32> value{};
        if(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0)return {};
        bool ok=BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)>=0;
        if(ok)ok=BCryptHashData(hash,const_cast<BYTE*>(bytes.data()),static_cast<ULONG>(bytes.size()),0)>=0&&BCryptFinishHash(hash,value.data(),32,0)>=0;
        if(hash)BCryptDestroyHash(hash);BCryptCloseAlgorithmProvider(algorithm,0);if(!ok)return {};
        constexpr char hex[]="0123456789abcdef";std::string text(64,'0');for(size_t i=0;i<32;i++){text[i*2]=hex[value[i]>>4];text[i*2+1]=hex[value[i]&15];}return text;
    }
    struct Candidate{ComPtr<ID3D11ShaderResourceView> view;bool accepted;};std::vector<Candidate> candidates;
    std::array<std::array<std::array<ComPtr<ID3D11ShaderResourceView>,11>,2>,4> views;
    HRESULT recognize(ID3D11DeviceContext* context,ID3D11ShaderResourceView* incoming,bool& accepted){
        accepted=false;D3D11_SHADER_RESOURCE_VIEW_DESC v{};incoming->GetDesc(&v);
        TraceTexture* observed=nullptr;
        if(!tracePath.empty()&&(v.Format==DXGI_FORMAT_BC3_UNORM||v.Format==DXGI_FORMAT_BC3_UNORM_SRGB)){
            for(auto& item:traceTextures)if(item.view==reinterpret_cast<uint64_t>(incoming)){observed=&item;break;}
            if(!observed&&traceTextures.size()<64){traceTextures.push_back({reinterpret_cast<uint64_t>(incoming),static_cast<uint32_t>(v.Format),static_cast<uint32_t>(v.ViewDimension),v.Texture2D.MostDetailedMip,v.Texture2D.MipLevels,0,0,0,0,0,0,false,{}});observed=&traceTextures.back();}
        }
        if(v.ViewDimension!=D3D11_SRV_DIMENSION_TEXTURE2D||(v.Format!=DXGI_FORMAT_BC3_UNORM&&v.Format!=DXGI_FORMAT_BC3_UNORM_SRGB)||v.Texture2D.MostDetailedMip)return S_OK;
        record(9);
        for(const auto& candidate:candidates)if(candidate.view.Get()==incoming){accepted=candidate.accepted;return S_OK;}
        ComPtr<ID3D11Resource> resource;incoming->GetResource(&resource);ComPtr<ID3D11Texture2D> texture;if(FAILED(resource.As(&texture)))return S_OK;
        D3D11_TEXTURE2D_DESC d{};texture->GetDesc(&d);if(observed){observed->width=d.Width;observed->height=d.Height;observed->textureFormat=d.Format;observed->mips=d.MipLevels;observed->array=d.ArraySize;observed->samples=d.SampleDesc.Count;}
        if(d.Width!=1024||d.Height!=1024||!d.MipLevels||d.MipLevels>11||!v.Texture2D.MipLevels||v.Texture2D.MipLevels>d.MipLevels||d.ArraySize!=1||d.SampleDesc.Count!=1||
            (d.Format!=DXGI_FORMAT_BC3_TYPELESS&&d.Format!=DXGI_FORMAT_BC3_UNORM&&d.Format!=DXGI_FORMAT_BC3_UNORM_SRGB))return S_OK;
        record(10);
        ComPtr<ID3D11Device> device;context->GetDevice(&device);d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;
        ComPtr<ID3D11Texture2D> staging;auto hr=device->CreateTexture2D(&d,nullptr,&staging);if(FAILED(hr))return hr;
        context->CopyResource(staging.Get(),texture.Get());D3D11_MAPPED_SUBRESOURCE mapped{};hr=context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped);if(FAILED(hr))return hr;
        std::vector<BYTE> original;
        try{if(mapped.pData&&mapped.RowPitch>=4096){original.resize(1048576);for(UINT row=0;row<256;row++)std::memcpy(original.data()+row*4096,static_cast<const BYTE*>(mapped.pData)+row*mapped.RowPitch,4096);}}
        catch(...){context->Unmap(staging.Get(),0);throw;}context->Unmap(staging.Get(),0);
        accepted=original.size()==1048576&&materialSha(original.data(),original.size(),"3bd4b1d2d18b50ad64613ca80dc06102b52f56877788767d10a618dfa9147bac");
        if(accepted&&sourceAlpha.empty())sourceAlpha=noticeBC3Alpha(original);
        record(11);if(observed){observed->hash=digest(original);observed->accepted=accepted;}if(accepted)record(12);
        if(candidates.size()>=32)candidates.erase(candidates.begin());candidates.push_back({incoming,accepted});return S_OK;
    }
public:
    explicit SeasonNoticeArt(const BYTE* data,std::wstring trace={}):pixels(data+32),tracePath(std::move(trace)){}
    void record(size_t stage)noexcept{if(!tracePath.empty())++stages[stage];}
    void saveTraceAfterDrain()const{
        if(tracePath.empty())return;std::ofstream out{std::filesystem::path(tracePath)};
        out<<"{\"stages\":[";for(size_t i=0;i<stages.size();i++){if(i)out<<',';out<<stages[i].load();}
        out<<"],\"textures\":[";bool first=true;for(const auto& item:traceTextures){if(!first)out<<',';first=false;
            out<<"{\"view\":"<<item.view<<",\"viewFormat\":"<<item.viewFormat<<",\"dimension\":"<<item.dimension<<",\"firstMip\":"<<item.firstMip<<",\"viewMips\":"<<item.viewMips<<",\"width\":"<<item.width<<",\"height\":"<<item.height
                <<",\"textureFormat\":"<<item.textureFormat<<",\"mips\":"<<item.mips<<",\"array\":"<<item.array<<",\"samples\":"<<item.samples<<",\"accepted\":"<<(item.accepted?"true":"false")<<",\"sha256\":\""<<item.hash<<"\"}";}
        out<<"]}";
    }
    uint64_t gpuBytes()const{uint64_t count=0;for(const auto& kinds:views)for(const auto& variants:kinds)for(size_t level=0;level<variants.size();level++)if(variants[level])for(size_t mip=0;mip<=level;mip++)count+=uint64_t(1024>>mip)*(1024>>mip)*4;return count;}
    HRESULT enter(ID3D11DeviceContext* context,uint32_t kind,NoticeArtTicket& ticket){
        if(kind<2||kind>5)return S_FALSE;
        ComPtr<ID3D11ShaderResourceView> incoming;context->PSGetShaderResources(0,1,&incoming);if(!incoming)return S_FALSE;
        record(8);
        bool accepted=false;auto hr=recognize(context,incoming.Get(),accepted);if(FAILED(hr)||!accepted)return FAILED(hr)?hr:S_FALSE;
        if(sourceAlpha.size()!=1024*1024)return E_UNEXPECTED;
        D3D11_SHADER_RESOURCE_VIEW_DESC original{};incoming->GetDesc(&original);uint32_t variant=original.Format==DXGI_FORMAT_BC3_UNORM_SRGB?1:0,index=kind-2;
        UINT levels=original.Texture2D.MipLevels;
        auto& replacement=views[index][variant][levels-1];if(!replacement){
            ComPtr<ID3D11Device> device;context->GetDevice(&device);
            D3D11_TEXTURE2D_DESC d{1024,1024,levels,1,DXGI_FORMAT_B8G8R8A8_TYPELESS,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
            std::array<std::vector<BYTE>,11> mips;std::array<D3D11_SUBRESOURCE_DATA,11> data{};
            auto* painting=pixels+size_t(index)*1024*1024*4;
            mips[0].assign(painting,painting+1024*1024*4);
            for(size_t pixel=0;pixel<sourceAlpha.size();pixel++)mips[0][pixel*4+3]=static_cast<BYTE>((uint32_t(painting[pixel*4+3])*sourceAlpha[pixel]+127)/255);
            data[0]={mips[0].data(),4096,1024*1024*4};
            for(UINT mip=1;mip<levels;mip++){
                UINT side=1024>>mip,previous=side*2;mips[mip].resize(size_t(side)*side*4);auto* source=static_cast<const BYTE*>(data[mip-1].pSysMem);
                for(UINT y=0;y<side;y++)for(UINT x=0;x<side;x++)for(UINT c=0;c<4;c++){
                    size_t at=(size_t(y)*2*previous+x*2)*4+c;
                    mips[mip][(size_t(y)*side+x)*4+c]=static_cast<BYTE>((uint32_t(source[at])+source[at+4]+source[at+previous*4]+source[at+previous*4+4]+2)/4);
                }
                data[mip]={mips[mip].data(),side*4,side*side*4};
            }
            ComPtr<ID3D11Texture2D> texture;
            hr=device->CreateTexture2D(&d,data.data(),&texture);if(FAILED(hr))return hr;
            D3D11_SHADER_RESOURCE_VIEW_DESC v{};v.Format=variant?DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:DXGI_FORMAT_B8G8R8A8_UNORM;v.ViewDimension=D3D11_SRV_DIMENSION_TEXTURE2D;v.Texture2D.MipLevels=levels;
            hr=device->CreateShaderResourceView(texture.Get(),&v,&replacement);if(FAILED(hr))return hr;
        }
        ticket.original=incoming;auto* next=replacement.Get();context->PSSetShaderResources(0,1,&next);ticket.changed=true;record(13);return S_OK;
    }
    bool leave(ID3D11DeviceContext* context,NoticeArtTicket& ticket){
        if(!ticket.changed)return true;auto* original=ticket.original.Get();context->PSSetShaderResources(0,1,&original);
        ComPtr<ID3D11ShaderResourceView> restored;context->PSGetShaderResources(0,1,&restored);ticket.changed=false;bool same=restored.Get()==original;if(same)record(14);return same;
    }
};
