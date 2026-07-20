// GaussianBlurV_D3D.hlsl - 洋红测试（阶段 1）
// 无条件输出纯洋红，验证 GPU 管线是否真的把 shader 输出呈现到了用户眼前
Texture2D Input : register(t0);
SamplerState Sampler : register(s0);
cbuffer CB : register(b0) { float2 TexelSize; };

float4 main(float4 pos : SV_POSITION, float2 uv : TEXCOORD) : SV_TARGET
{
    return float4(1, 0, 1, 1);   // 无条件纯洋红
}