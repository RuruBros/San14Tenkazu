#pragma once
#include <cwchar>

// Explicit diagnostic request only. At most two real weather frames, all GPU
// reads on the admitted render callback; file I/O only after callback drain.
class SeasonWeatherCapture {
    std::wstring path;uint32_t limit=0,frames=0;uint64_t next=0;
    HRESULT error=S_OK;bool active=false,singlePass=false;
    std::vector<BYTE> output,frame;
    static void append(std::vector<BYTE>& out,const void* value,size_t size){
        auto p=static_cast<const BYTE*>(value);out.insert(out.end(),p,p+size);
    }
    HRESULT texture(ID3D11DeviceContext* context,ID3D11Texture2D* source,std::vector<BYTE>& out){
        D3D11_TEXTURE2D_DESC d{};source->GetDesc(&d);
        if(d.Width>2048||d.Height>2048||d.ArraySize!=1||d.MipLevels!=1||d.SampleDesc.Count!=1)return E_INVALIDARG;
        UINT pixelBytes=d.Format==DXGI_FORMAT_R32G8X24_TYPELESS?8:4;
        d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;
        ComPtr<ID3D11Device> device;context->GetDevice(&device);ComPtr<ID3D11Texture2D> staging;
        HRESULT hr=device->CreateTexture2D(&d,nullptr,&staging);if(FAILED(hr))return hr;
        context->CopyResource(staging.Get(),source);D3D11_MAPPED_SUBRESOURCE mapped{};
        hr=context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped);if(FAILED(hr))return hr;
        // begin() reserves the full frame before any Map.
        for(UINT y=0;y<d.Height;y++)append(out,static_cast<const BYTE*>(mapped.pData)+size_t(y)*mapped.RowPitch,size_t(d.Width)*pixelBytes);
        context->Unmap(staging.Get(),0);return S_OK;
    }
public:
    void configure(std::wstring destination,uint32_t count,bool onePass=false){path=std::move(destination);limit=count;singlePass=onePass;}
    bool requested()const{return limit!=0;}
    void begin(ID3D11DeviceContext* context,ID3D11Texture2D* color,ID3D11Texture2D* depth,
               ID3D11ShaderResourceView* matrix,const void* parameters,const void* weatherParameters,uint32_t year,uint32_t month,uint32_t day){
        active=false;if(!limit||frames>=limit||FAILED(error)||GetTickCount64()<next)return;
        next=GetTickCount64()+1000;D3D11_TEXTURE2D_DESC c{},d{};color->GetDesc(&c);depth->GetDesc(&d);
        if(c.Width>2048||c.Height>2048){error=E_INVALIDARG;return;}
        frame.clear();frame.reserve(32+80+144+size_t(c.Width)*c.Height*20);
        uint32_t header[]={c.Width,c.Height,uint32_t(c.Format),uint32_t(d.Format),year,month,day,0};
        append(frame,header,sizeof(header));append(frame,parameters,64);append(frame,weatherParameters,16);
        ComPtr<ID3D11Resource> resource;matrix->GetResource(&resource);ComPtr<ID3D11Buffer> original;
        error=resource.As(&original);if(FAILED(error))return;
        D3D11_BUFFER_DESC bd{};original->GetDesc(&bd);if(bd.ByteWidth!=144){error=E_INVALIDARG;return;}
        bd.Usage=D3D11_USAGE_STAGING;bd.BindFlags=0;bd.CPUAccessFlags=D3D11_CPU_ACCESS_READ;bd.MiscFlags=0;bd.StructureByteStride=0;
        ComPtr<ID3D11Device> device;context->GetDevice(&device);ComPtr<ID3D11Buffer> staging;
        error=device->CreateBuffer(&bd,nullptr,&staging);if(FAILED(error))return;
        context->CopyResource(staging.Get(),original.Get());D3D11_MAPPED_SUBRESOURCE mapped{};
        error=context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped);if(FAILED(error))return;
        append(frame,mapped.pData,144);context->Unmap(staging.Get(),0);
        error=texture(context,depth,frame);if(FAILED(error))return;
        error=texture(context,color,frame);active=SUCCEEDED(error);
    }
    void afterHaze(ID3D11DeviceContext* context,ID3D11Texture2D* color){
        if(active){error=texture(context,color,frame);active=SUCCEEDED(error);}
    }
    // Rice evidence: original depth, before/after rice, the same real camera,
    // date and clock. Separate RCAP v1 file, never mixed into WCAP v2 frames.
    void afterPass(ID3D11DeviceContext* context,ID3D11Texture2D* color){
        if(!active||!singlePass)return;error=texture(context,color,frame);active=false;
        if(SUCCEEDED(error)){append(output,frame.data(),frame.size());++frames;}
    }
    void afterSnow(ID3D11DeviceContext* context,ID3D11Texture2D* color){
        if(!active)return;error=texture(context,color,frame);active=false;
        if(SUCCEEDED(error)){append(output,frame.data(),frame.size());++frames;}
    }
    // Called by the lease/pipe worker after hooks are detached and drained.
    void save()const{
        if(!limit)return;HANDLE file=CreateFileW(path.c_str(),GENERIC_WRITE,0,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(file==INVALID_HANDLE_VALUE)return;DWORD written{};
        uint32_t header[]={singlePass?0x52434150u:0x57434150u,singlePass?1u:2u,frames,uint32_t(error)};
        bool ok=WriteFile(file,header,sizeof(header),&written,nullptr)&&written==sizeof(header);
        if(ok&&!output.empty())WriteFile(file,output.data(),static_cast<DWORD>(output.size()),&written,nullptr);
        CloseHandle(file);
    }
};
