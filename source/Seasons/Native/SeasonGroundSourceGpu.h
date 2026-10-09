#pragma once
// Original terrain sources, authenticated in D3D subresource/channel order.
// Installation G1T arrays use mip-major order and have a different file hash.
class SeasonGroundSources {
    uint32_t rejected=0;
    ComPtr<ID3D11ShaderResourceView> controlReference;
    ComPtr<ID3D11ShaderResourceView> detailReference;
    struct Identity {ComPtr<ID3D11ShaderResourceView> view;uint32_t slot;bool valid;};
    std::array<Identity,16> identities;size_t next=0;
    HRESULT verify(ID3D11DeviceContext* context,ID3D11ShaderResourceView* view,uint32_t slot){
        for(const auto& i:identities)if(i.view.Get()==view&&i.slot==slot)return i.valid?S_OK:S_FALSE;
        ComPtr<ID3D11Resource> resource;view->GetResource(&resource);ComPtr<ID3D11Texture2D> texture;
        if(FAILED(resource.As(&texture)))return S_FALSE;
        D3D11_TEXTURE2D_DESC d{};texture->GetDesc(&d);D3D11_SHADER_RESOURCE_VIEW_DESC v{};view->GetDesc(&v);
        bool array=slot==7,control=slot==3;UINT size=array?1024:control?2049:4096,mips=array?9:control?1:11,layers=array?19:1;
        bool shape=d.Width==size&&d.Height==size&&d.MipLevels==mips&&d.ArraySize==layers&&d.SampleDesc.Count==1&&d.SampleDesc.Quality==0&&d.CPUAccessFlags==0&&
            d.Format==(control?DXGI_FORMAT_R8G8B8A8_TYPELESS:DXGI_FORMAT_BC3_TYPELESS)&&
            (control?(d.Usage==D3D11_USAGE_IMMUTABLE||d.Usage==D3D11_USAGE_DEFAULT):d.Usage==D3D11_USAGE_IMMUTABLE)&&
            d.BindFlags==UINT(control&&d.Usage==D3D11_USAGE_DEFAULT?(D3D11_BIND_SHADER_RESOURCE|D3D11_BIND_RENDER_TARGET|D3D11_BIND_UNORDERED_ACCESS):D3D11_BIND_SHADER_RESOURCE)&&d.MiscFlags==0&&
            (control?v.Format==DXGI_FORMAT_R8G8B8A8_UNORM:(v.Format==DXGI_FORMAT_BC3_UNORM||v.Format==DXGI_FORMAT_BC3_UNORM_SRGB))&&
            (array?(v.ViewDimension==D3D11_SRV_DIMENSION_TEXTURE2DARRAY&&v.Texture2DArray.FirstArraySlice==0&&v.Texture2DArray.ArraySize==19&&v.Texture2DArray.MostDetailedMip==0&&v.Texture2DArray.MipLevels==mips):
                (v.ViewDimension==D3D11_SRV_DIMENSION_TEXTURE2D&&v.Texture2D.MostDetailedMip==0&&v.Texture2D.MipLevels==mips));
        bool accepted=false;
        if(shape){
            ComPtr<ID3D11Device> device;context->GetDevice(&device);d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
            ComPtr<ID3D11Texture2D> staging;auto hr=device->CreateTexture2D(&d,nullptr,&staging);if(FAILED(hr))return hr;context->CopyResource(staging.Get(),texture.Get());
            std::vector<BYTE> bytes;bytes.reserve(array?26563824:control?16793604:22369616);
            for(UINT layer=0;layer<layers;layer++)for(UINT mip=0;mip<mips;mip++){
                UINT pitch=control?size*4:std::max(1u,((size>>mip)+3)/4)*16,rows=control?size:std::max(1u,((size>>mip)+3)/4);auto at=bytes.size();bytes.resize(at+size_t(pitch)*rows);
                UINT sub=D3D11CalcSubresource(mip,layer,mips);D3D11_MAPPED_SUBRESOURCE mapped{};hr=context->Map(staging.Get(),sub,D3D11_MAP_READ,0,&mapped);if(FAILED(hr))return hr;
                bool valid=mapped.pData&&mapped.RowPitch>=pitch;
                if(valid)for(UINT y=0;y<rows;y++)std::memcpy(bytes.data()+at+y*pitch,static_cast<BYTE*>(mapped.pData)+y*mapped.RowPitch,pitch);
                context->Unmap(staging.Get(),sub);if(!valid)return E_INVALIDARG;
            }
            // Installed control payload is BGRA; the game's GPU upload is RGBA.
            // Full byte equality after R/B conversion was recorded separately.
            accepted=materialSha(bytes.data(),bytes.size(),array?"b7a75ee73b1dd3dfc98680e7fd0af23ddb80673b0fa4736854984fe5df75f574":control?"1272971b34489b7ea1881ac4e61579d590b727689df719c68a63c3f85b34e49e":
                "05c7b468e51b026ca017ed4316b4f2fef855b4da6e2dc11d6f5921898f0f45cd");
            if(accepted&&control&&!controlReference){
                // The game's control is DEFAULT, so its COM identity cannot
                // prove later contents. Keep an immutable authenticated copy;
                // RiceCrop compares live samples against this copy every Draw.
                D3D11_TEXTURE2D_DESC owned{size,size,1,1,DXGI_FORMAT_R8G8B8A8_TYPELESS,{1,0},D3D11_USAGE_IMMUTABLE,D3D11_BIND_SHADER_RESOURCE,0,0};
                D3D11_SUBRESOURCE_DATA initial{bytes.data(),size*4,0};ComPtr<ID3D11Texture2D> canonical;
                hr=device->CreateTexture2D(&owned,&initial,&canonical);if(FAILED(hr))return hr;
                hr=device->CreateShaderResourceView(canonical.Get(),&v,&controlReference);if(FAILED(hr))return hr;
            }
        }
        if(!accepted)rejected=shape?(0x40000000u|slot):((slot<<24)|(uint32_t(d.Format)<<16)|(uint32_t(d.Usage)<<12)|(d.MipLevels<<4)|(array?1u:0u));
        if(accepted&&array)detailReference=view;
        identities[next]={view,slot,accepted};next=(next+1)%identities.size();return accepted?S_OK:S_FALSE;
    }
public:
#ifdef SEASON_BRIDGE_FIXTURE
    HRESULT fixtureControl(ID3D11DeviceContext* context,ID3D11ShaderResourceView* view){return verify(context,view,3);}
#endif
    uint32_t rejection()const{return rejected;}
    void clear(){for(auto& i:identities)i={};controlReference.Reset();detailReference.Reset();next=0;rejected=0;}
    ID3D11ShaderResourceView* riceControlReference()const{return controlReference.Get();}
    ID3D11ShaderResourceView* vegetationDetailReference()const{return detailReference.Get();}
    uint64_t gpuBytes()const{return controlReference?16793604:0;}
    HRESULT admit(ID3D11DeviceContext* context,bool detailed=true,bool rice=false){
        for(UINT slot:{3u,4u,7u}){if((slot==3&&!rice)||(slot==7&&!detailed))continue;ComPtr<ID3D11ShaderResourceView> view;context->PSGetShaderResources(slot,1,&view);if(!view){rejected=0x80000000u|slot;return S_FALSE;}
            auto hr=verify(context,view.Get(),slot);if(hr!=S_OK)return hr;}return S_OK;
    }
};
