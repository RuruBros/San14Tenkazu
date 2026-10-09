// Saved actual game matrices + independent double-precision CPU inversion.
// This program owns its WARP device; it cannot attach to the game.
#define NOMINMAX
#include "SeasonWorldGpu.h"
#include <array>
#include <fstream>
#include <filesystem>
#include <iostream>
#include <cmath>
#include <algorithm>
using Microsoft::WRL::ComPtr;
using Matrix=std::array<std::array<double,4>,4>;
static void require(bool value,const char* why){if(!value)throw std::runtime_error(why);}
static void hr(HRESULT value){require(SUCCEEDED(value),"World matrix GPU API failed");}
static Matrix inverse(Matrix m){
    Matrix out{};for(size_t i=0;i<4;i++)out[i][i]=1;
    for(size_t c=0;c<4;c++){
        size_t pivot=c;for(size_t r=c+1;r<4;r++)if(std::abs(m[r][c])>std::abs(m[pivot][c]))pivot=r;
        require(std::abs(m[pivot][c])>1e-25,"Independent CPU singular matrix");
        std::swap(m[c],m[pivot]);std::swap(out[c],out[pivot]);double d=m[c][c];
        for(size_t j=0;j<4;j++){m[c][j]/=d;out[c][j]/=d;}
        for(size_t r=0;r<4;r++)if(r!=c){double f=m[r][c];for(size_t j=0;j<4;j++){m[r][j]-=f*m[c][j];out[r][j]-=f*out[c][j];}}
    }return out;
}
int wmain(int argc,wchar_t** argv){
    try{
        require(argc>=2,"Usage: SeasonWorldChecks.exe saved-VS-CB0.bin [...]");
        std::vector<Matrix> cases;for(int a=1;a<argc;a++){
            std::ifstream file(std::filesystem::path(argv[a]),std::ios::binary);float values[16]{};
            file.read(reinterpret_cast<char*>(values),sizeof(values));require(bool(file),"Saved game matrix missing");
            Matrix m{};for(size_t i=0;i<16;i++){require(std::isfinite(values[i]),"Saved matrix nonfinite");m[i/4][i%4]=values[i];}cases.push_back(m);
        }
        const auto actual=cases.size();
        cases.push_back({{{1,0,0,0},{0,1,0,0},{0,0,1,0},{0,0,0,1}}});
        cases.push_back({{{.001,0,0,0},{0,.002,0,0},{0,0,.0001,0},{-100,80,-1,1}}});
        // Row-vector perspective, translated camera; catches transposition and
        // division by W errors that an identity-only fixture cannot detect.
        cases.push_back({{{1.7,0,0,0},{0,2.1,0,0},{0,0,1.00001,1},{-3400,6300,-12010,-12000}}});
        ComPtr<ID3D11Device> device;ComPtr<ID3D11DeviceContext> context;
        hr(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context));
        SeasonWorldGpu gpu;double maxRelative=0,maxWorldError=0;size_t checks=0;
        auto read=[&](){
            D3D11_BUFFER_DESC d{};gpu.data()->GetDesc(&d);d.Usage=D3D11_USAGE_STAGING;d.BindFlags=0;d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;d.MiscFlags=0;d.StructureByteStride=0;
            ComPtr<ID3D11Buffer> staging;hr(device->CreateBuffer(&d,nullptr,&staging));context->CopyResource(staging.Get(),gpu.data());
            D3D11_MAPPED_SUBRESOURCE mapped{};hr(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped));
            std::array<float,36> values;std::memcpy(values.data(),mapped.pData,sizeof(values));context->Unmap(staging.Get(),0);return values;
        };
        auto constants=[&](const Matrix& m){
            float values[16];for(size_t i=0;i<16;i++)values[i]=static_cast<float>(m[i/4][i%4]);
            D3D11_BUFFER_DESC d{64,D3D11_USAGE_IMMUTABLE,D3D11_BIND_CONSTANT_BUFFER,0,0,0};D3D11_SUBRESOURCE_DATA data{values,0,0};
            ComPtr<ID3D11Buffer> buffer;hr(device->CreateBuffer(&d,&data,&buffer));return buffer;
        };
        for(const auto& m:cases){
            auto buffer=constants(m);auto* saved=buffer.Get();context->VSSetConstantBuffers(0,1,&saved);context->CSSetConstantBuffers(3,1,&saved);
            hr(gpu.update(context.Get(),buffer.Get()));auto values=read();require(values[32]==1,"Valid camera was rejected");
            ComPtr<ID3D11Buffer> afterVs,afterCs;context->VSGetConstantBuffers(0,1,&afterVs);context->CSGetConstantBuffers(3,1,&afterCs);
            require(afterVs==buffer&&afterCs==buffer,"Game constant bindings not restored");
            // Compare to the actual float values submitted, not unquantized literals.
            Matrix submitted{};for(size_t i=0;i<16;i++)submitted[i/4][i%4]=static_cast<float>(m[i/4][i%4]);auto expected=inverse(submitted);
            for(size_t i=0;i<16;i++){
                require(values[i]==static_cast<float>(m[i/4][i%4]),"World-to-clip rows changed");
                double error=std::abs(values[16+i]-expected[i/4][i%4])/std::max(1.,std::abs(expected[i/4][i%4]));
                maxRelative=std::max(maxRelative,error);require(std::isfinite(error)&&error<.002,"GPU inverse differs from CPU");checks++;
            }
            // Independent clip-space samples mapped to game-space using both inverses.
            for(double x:{-.8,0.,.8})for(double y:{-.8,0.,.8})for(double z:{.1,.5,.9}){
                std::array<double,4> clip{x,y,z,1},reference{},result{};
                for(size_t j=0;j<4;j++)for(size_t i=0;i<4;i++){reference[j]+=clip[i]*expected[i][j];result[j]+=clip[i]*values[16+i*4+j];}
                if(std::abs(reference[3])<1e-6||std::abs(result[3])<1e-6)continue;
                for(size_t j=0;j<3;j++){
                    double error=std::abs(reference[j]/reference[3]-result[j]/result[3]);maxWorldError=std::max(maxWorldError,error);
                    require(std::isfinite(error)&&error<2,"GPU game-space reconstruction exceeds two game units");checks++;
                }
            }
        }
        Matrix singular{};auto bad=constants(singular);hr(gpu.update(context.Get(),bad.Get()));require(read()[32]==0,"Singular camera accepted");
        singular[0][0]=std::numeric_limits<double>::quiet_NaN();bad=constants(singular);hr(gpu.update(context.Get(),bad.Get()));require(read()[32]==0,"Nonfinite camera accepted");
        std::cout<<"{\"actualSavedMatrices\":"<<actual<<",\"matrixCases\":"<<cases.size()<<",\"checks\":"<<checks
            <<",\"maxRelativeInverseError\":"<<maxRelative<<",\"maxWorldUnitsError\":"<<maxWorldError
            <<",\"singularAndNonfiniteRejected\":true,\"constantBindingsRestored\":true,\"gameScreenVerified\":false}\n";
        return 0;
    }catch(const std::exception& error){std::cerr<<error.what()<<"\n";return 1;}
}
