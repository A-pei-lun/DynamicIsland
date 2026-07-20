// GaussianBlurV_D3D.hlsl - 可分离高斯模糊 · 垂直遍（D3D11 ps_4_0）
// 9-tap 核 σ≈1.5，同 GaussianBlurV.hlsl(ps_3_0) 算法。TexelSize.y = radius/height 控跨距。
// cbuffer CB(b0)：C# 侧 BlurCB{TexelSizeX, TexelSizeY, Pad0, Pad1}，本 shader 读 .y。
// 编译：fxc /T ps_4_0 /O3 /E main /Fo GaussianBlurV_D3D.cso GaussianBlurV_D3D.hlsl
Texture2D Input : register(t0);
SamplerState Sampler : register(s0);
cbuffer CB : register(b0) { float2 TexelSize; };

float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD) : SV_TARGET
{
    float4 color = 0;
    float weight[9] = { 0.016, 0.054, 0.122, 0.184, 0.248, 0.184, 0.122, 0.054, 0.016 };
    float offset[9] = { -4, -3, -2, -1, 0, 1, 2, 3, 4 };
    [unroll]
    for (int i = 0; i < 9; i++)
        color += Input.Sample(Sampler, uv + float2(0, offset[i] * TexelSize.y)) * weight[i];
    return color;
}
