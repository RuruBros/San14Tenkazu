// The authenticated game VS uses mul(float4(world,1), row_major mW2P).
// Input is the actual bound VS b0; it is read on the GPU, never written.
cbuffer GameTransform : register(b0) { row_major float4x4 GameWorldToClip; };
RWStructuredBuffer<float4> WorldMatrices : register(u0);
[numthreads(1,1,1)]
void BuildWorldMatrices(uint3 id : SV_DispatchThreadID)
{
    float4 rows[4]; float4 inverse[4];
    [unroll] for(uint i=0;i<4;i++)
    {
        rows[i]=GameWorldToClip[i];
        inverse[i]=float4(i==0,i==1,i==2,i==3);
        WorldMatrices[i]=rows[i];
    }
    bool valid=true;
    // Scaled partial pivoting keeps world-unit translations from choosing
    // an unstable pivot. A singular/nonfinite transform is rejected, not
    // replaced with an identity camera.
    [unroll] for(uint column=0;column<4;column++)
    {
        uint pivot=column; float best=-1;
        [unroll] for(uint row=column;row<4;row++)
        {
            float scale=max(max(abs(rows[row].x),abs(rows[row].y)),max(abs(rows[row].z),abs(rows[row].w)));
            float score=scale>0?abs(rows[row][column])/scale:0;
            if(score>best){best=score;pivot=row;}
        }
        float4 temporary=rows[column];rows[column]=rows[pivot];rows[pivot]=temporary;
        temporary=inverse[column];inverse[column]=inverse[pivot];inverse[pivot]=temporary;
        float denominator=rows[column][column];
        valid=valid&&all(isfinite(rows[column]))&&isfinite(denominator)&&abs(denominator)>1e-20;
        float reciprocal=valid?1/denominator:0;
        rows[column]*=reciprocal;inverse[column]*=reciprocal;
        [unroll] for(uint other=0;other<4;other++) if(other!=column)
        {
            float factor=rows[other][column];
            rows[other]-=factor*rows[column];inverse[other]-=factor*inverse[column];
        }
    }
    [unroll] for(uint j=0;j<4;j++)valid=valid&&all(isfinite(inverse[j]));
    [unroll] for(uint k=0;k<4;k++)WorldMatrices[4+k]=valid?inverse[k]:0;
    WorldMatrices[8]=float4(valid?1:0,0,0,0);
}
